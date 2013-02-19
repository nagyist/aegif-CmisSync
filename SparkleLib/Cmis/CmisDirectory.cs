﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Data.SQLite;
using DotCMIS.Client;
using DotCMIS;
using DotCMIS.Client.Impl;
using DotCMIS.Exceptions;
using DotCMIS.Enums;
using System.ComponentModel;
using System.Collections;
using DotCMIS.Data.Impl;

using System.Net;

namespace SparkleLib.Cmis
{
    public partial class SparkleRepoCmis : SparkleRepoBase
    {
        private enum RulesType { Folder, File };

        /**
         * Synchronization with a particular CMIS folder.
         */
        private partial class CmisDirectory
        {
            /**
             * Whether sync is bidirectional or only from server to client.
             * TODO make it a CMIS folder - specific setting
             */
            private bool BIDIRECTIONAL = true;

            /**
             * At which degree the repository supports Change Logs.
             * See http://docs.oasis-open.org/cmis/CMIS/v1.0/os/cmis-spec-v1.0.html#_Toc243905424
             * Possible values: none, objectidsonly, properties, all
             */
            private bool ChangeLogCapability;

            /**
             * Session to the CMIS repository.
             */
            private ISession session;

            /**
             * Local folder where the changes are synchronized to.
             * Example: "C:\CmisSync"
             */
            private string localRootFolder;

            /**
             * Path of the root in the remote repository.
             * Example: "/User Homes/nicolas.raoul/demos"
             */
            private string remoteFolderPath;

            /**
             * Syncing lock.
             * true if syncing is being performed right now.
             * TODO use is_syncing variable in parent
             */
            private bool syncing = true;

            /**
             * Parameters to use for all CMIS requests.
             */
            private Dictionary<string, string> cmisParameters;

            /**
             * Database to cache remote information from the CMIS server.
             */
            private CmisDatabase database;

            /**
             * Listener we inform about activity (used by spinner)
             */
            private ActivityListener activityListener;

            /**
             * Config 
             * */
            private SparkleRepoInfo repoinfo;

            // Why use a special constructor, add folder in config before syncing and use standard constructor instead
            /**
             * Constructor for SparkleFetcher (when a new CMIS folder is first added)
             * 
             */
            //public CmisDirectory(string canonical_name, string localPath, string remoteFolderPath,
            //    string url, string user, string password, string repositoryId,
            //    ActivityListener activityListener)
            //{
            //    this.activityListener = activityListener;
            //    this.remoteFolderPath = remoteFolderPath;

            //    // Set local root folder.
            //    this.localRootFolder = Path.Combine(SparkleFolder.ROOT_FOLDER, canonical_name);

            //    // Database is place in appdata of the users instead of sync folder (more secure)
            //    // database = new CmisDatabase(localRootFolder);
            //    string cmis_path = Path.Combine(config.ConfigPath, canonical_name + ".cmissync");
            //    database = new CmisDatabase(cmis_path);

            //    cmisParameters = new Dictionary<string, string>();
            //    cmisParameters[SessionParameter.BindingType] = BindingType.AtomPub;
            //    cmisParameters[SessionParameter.AtomPubUrl] = url;
            //    cmisParameters[SessionParameter.User] = user;
            //    cmisParameters[SessionParameter.Password] = password;
            //    cmisParameters[SessionParameter.RepositoryId] = repositoryId;

            //    syncing = false;
            //}


            /**
             * Constructor for SparkleRepo (at every launch of CmisSync)
             */
            public CmisDirectory(string localPath, SparkleRepoInfo repoInfo,
                ActivityListener activityListener)
            {
                this.activityListener = activityListener;
                this.repoinfo = repoInfo;
                // Set local root folder
                String FolderName = Path.GetFileName(localPath);
                this.localRootFolder = Path.Combine(SparkleFolder.ROOT_FOLDER, FolderName);

                // Database is place in appdata of the users instead of sync folder (more secure)
                // database = new CmisDatabase(localRootFolder);
                database = new CmisDatabase(repoinfo.CmisDatabase);

                // Get path on remote repository.
                remoteFolderPath = repoInfo.RemotePath;

                cmisParameters = new Dictionary<string, string>();
                cmisParameters[SessionParameter.BindingType] = BindingType.AtomPub;
                cmisParameters[SessionParameter.AtomPubUrl] = repoInfo.Address.ToString();
                cmisParameters[SessionParameter.User] = repoInfo.User;
                // Uncrypt password
                cmisParameters[SessionParameter.Password] = CmisCrypto.Unprotect(repoInfo.Password);
                cmisParameters[SessionParameter.RepositoryId] = repoInfo.RepoID;

                cmisParameters[SessionParameter.ConnectTimeout] = "-1";

                syncing = false;

            }


            /**
             * Connect to the CMIS repository.
             */
            public void Connect()
            {
                do
                {
                    try
                    {
                        // Create session factory.
                        SessionFactory factory = SessionFactory.NewInstance();
                        session = factory.CreateSession(cmisParameters);

                        // Detect whether the repository has the ChangeLog capability.
                        ChangeLogCapability = session.RepositoryInfo.Capabilities.ChangesCapability == CapabilityChanges.All
                                || session.RepositoryInfo.Capabilities.ChangesCapability == CapabilityChanges.ObjectIdsOnly;
                        SparkleLogger.LogInfo("Sync", "ChangeLog capability: " + ChangeLogCapability);
                        SparkleLogger.LogInfo("Sync", "Created CMIS session: " + session.ToString());
                    }
                    catch (CmisRuntimeException e)
                    {
                        SparkleLogger.LogInfo("Sync", "Exception: " + e.Message + ", error content: " + e.ErrorContent);
                    }
                    if (session == null)
                    {
                        SparkleLogger.LogInfo("Sync", "Connection failed, waiting for 10 seconds: " + this.localRootFolder + "(" + cmisParameters[SessionParameter.AtomPubUrl] + ")");
                        System.Threading.Thread.Sleep(10 * 1000);
                    }
                }
                while (session == null);
            }


            private void ChangeLogSync(IFolder remoteFolder)
            {
                // Get last change log token on server side.
                string lastTokenOnServer = session.Binding.GetRepositoryService().GetRepositoryInfo(session.RepositoryInfo.Id, null).LatestChangeLogToken;

                // Get last change token that had been saved on client side.
                string lastTokenOnClient = database.GetChangeLogToken();

                if (lastTokenOnClient == null)
                {
                    // Token is null, which means no sync has ever happened yet, so just copy everything.
                    RecursiveFolderCopy(remoteFolder, localRootFolder);
                }
                else
                {
                    // If there are remote changes, apply them.
                    if (lastTokenOnServer.Equals(lastTokenOnClient))
                    {
                        SparkleLogger.LogInfo("Sync", "No changes on server, ChangeLog token: " + lastTokenOnServer);
                    }
                    else
                    {
                        // Check which files/folders have changed.
                        int maxNumItems = 1000;
                        IChangeEvents changes = session.GetContentChanges(lastTokenOnClient, true, maxNumItems);

                        // Replicate each change to the local side.
                        foreach (IChangeEvent change in changes.ChangeEventList)
                        {
                            ApplyRemoteChange(change);
                        }

                        // Save change log token locally.
                        // TODO only if successful
                        SparkleLogger.LogInfo("Sync", "Updating ChangeLog token: " + lastTokenOnServer);
                        database.SetChangeLogToken(lastTokenOnServer);
                    }

                    // Upload local changes by comparing with database.
                    // TODO
                }
            }


            /**
             * Apply a remote change.
             */
            private void ApplyRemoteChange(IChangeEvent change)
            {
                SparkleLogger.LogInfo("Sync", "Change type:" + change.ChangeType + " id:" + change.ObjectId + " properties:" + change.Properties);
                switch (change.ChangeType)
                {
                    case ChangeType.Created:
                    case ChangeType.Updated:
                        ICmisObject cmisObject = session.GetObject(change.ObjectId);
                        if (cmisObject is DotCMIS.Client.Impl.Folder)
                        {
                            IFolder remoteFolder = (IFolder)cmisObject;
                            string localFolder = Path.Combine(localRootFolder, remoteFolder.Path);
                            RecursiveFolderCopy(remoteFolder, localFolder);
                        }
                        else if (cmisObject is DotCMIS.Client.Impl.Document)
                        {
                            IDocument remoteDocument = (IDocument)cmisObject;
                            string remoteDocumentPath = remoteDocument.Paths.First();
                            if (!remoteDocumentPath.StartsWith(remoteFolderPath))
                            {
                                SparkleLogger.LogInfo("Sync", "Change in unrelated document: " + remoteDocumentPath);
                                break; // The change is not under the folder we care about.
                            }
                            string relativePath = remoteDocumentPath.Substring(remoteFolderPath.Length + 1);
                            string relativeFolderPath = Path.GetDirectoryName(relativePath);
                            relativeFolderPath = relativeFolderPath.Replace("/", "\\"); // TODO OS-specific separator
                            string localFolderPath = Path.Combine(localRootFolder, relativeFolderPath);
                            DownloadFile(remoteDocument, localFolderPath);
                        }
                        break;
                    case ChangeType.Deleted:
                        cmisObject = session.GetObject(change.ObjectId);
                        if (cmisObject is DotCMIS.Client.Impl.Folder)
                        {
                            IFolder remoteFolder = (IFolder)cmisObject;
                            string localFolder = Path.Combine(localRootFolder, remoteFolder.Path);
                            RemoveFolderLocally(localFolder); // Remove from filesystem and database.
                        }
                        else if (cmisObject is DotCMIS.Client.Impl.Document)
                        {
                            IDocument remoteDocument = (IDocument)cmisObject;
                            string remoteDocumentPath = remoteDocument.Paths.First();
                            if (!remoteDocumentPath.StartsWith(remoteFolderPath))
                            {
                                SparkleLogger.LogInfo("Sync", "Change in unrelated document: " + remoteDocumentPath);
                                break; // The change is not under the folder we care about.
                            }
                            string relativePath = remoteDocumentPath.Substring(remoteFolderPath.Length + 1);
                            string relativeFolderPath = Path.GetDirectoryName(relativePath);
                            relativeFolderPath = relativeFolderPath.Replace("/", "\\"); // TODO OS-specific separator
                            string localFolderPath = Path.Combine(localRootFolder, relativeFolderPath);
                            // TODO DeleteFile(localFolderPath); // Delete on filesystem and in database
                        }
                        break;
                    case ChangeType.Security:
                        break;
                }
            }


            /**
             * Download all content from a CMIS folder.
             */
            private void RecursiveFolderCopy(IFolder remoteFolder, string localFolder)
            {
                activityListener.ActivityStarted();
                // List all children.
                foreach (ICmisObject cmisObject in remoteFolder.GetChildren())
                {
                    if (cmisObject is DotCMIS.Client.Impl.Folder)
                    {
                        IFolder remoteSubFolder = (IFolder)cmisObject;
                        string localSubFolder = localFolder + Path.DirectorySeparatorChar + cmisObject.Name;

                        // Create local folder.
                        Directory.CreateDirectory(localSubFolder);

                        // Create database entry for this folder.
                        database.AddFolder(localSubFolder, remoteFolder.LastModificationDate);

                        // Recurse into folder.
                        RecursiveFolderCopy(remoteSubFolder, localSubFolder);
                    }
                    else
                    {
                        // It is a file, just download it.
                        DownloadFile((IDocument)cmisObject, localFolder);
                    }
                }
                activityListener.ActivityStopped();
            }


            /**
             * Download a single file from the CMIS server.
             * Full rewrite by Yannick
             */
            private void DownloadFile(IDocument remoteDocument, string localFolder)
            {
                activityListener.ActivityStarted();

                string filepath = Path.Combine(localFolder, remoteDocument.ContentStreamFileName);

                // If a file exist, file is deleted.
                if (File.Exists(filepath))
                    File.Delete(filepath);

                string tmpfilepath = filepath + ".sync";

                // Create Stream with the local file in append mode, if file is empty it's like a full download.
                StreamWriter localfile = new StreamWriter(tmpfilepath, true);
                localfile.AutoFlush = true;
                DotCMIS.Data.IContentStream contentStream = null;

                // Download file, starting at the last download point

                // Get the last position in the localfile.
                Boolean success = false;
                try
                {
                    Int64 Offset = localfile.BaseStream.Position;

                    contentStream = remoteDocument.GetContentStream(remoteDocument.Id, Offset, remoteDocument.ContentStreamLength);
                    if (contentStream == null)
                    {
                        SparkleLogger.LogInfo("CmisDirectory", "Skipping download of file with null content stream: " + remoteDocument.ContentStreamFileName);
                        throw new IOException();
                    }

                    SparkleLogger.LogInfo("CmisDirectory", String.Format("Start download of file with offset {0}", Offset));

                    CopyStream(contentStream.Stream, localfile.BaseStream);
                    localfile.Flush();
                    localfile.Close();
                    contentStream.Stream.Close();
                    success = true;
                }
                catch (Exception ex)
                {
                    SparkleLogger.LogInfo("CmisDirectory", String.Format("Download of file {0} abort: {1}", remoteDocument.ContentStreamFileName, ex));
                    success = false;
                    localfile.Flush();
                    localfile.Close();
                    if (contentStream != null) contentStream.Stream.Close();
                }
                // Rename file
                // TODO - Yannick - Control file integrity by using hash compare - Is it necessary ?
                if (success)
                {
                    File.Move(tmpfilepath, filepath);

                    // Get metadata.
                    Dictionary<string, string> metadata = new Dictionary<string, string>();
                    metadata.Add("Id", remoteDocument.Id);
                    metadata.Add("VersionSeriesId", remoteDocument.VersionSeriesId);
                    metadata.Add("VersionLabel", remoteDocument.VersionLabel);
                    metadata.Add("CreationDate", remoteDocument.CreationDate.ToString());
                    metadata.Add("CreatedBy", remoteDocument.CreatedBy);
                    metadata.Add("lastModifiedBy", remoteDocument.LastModifiedBy);
                    metadata.Add("CheckinComment", remoteDocument.CheckinComment);
                    metadata.Add("IsImmutable", (bool)(remoteDocument.IsImmutable) ? "true" : "false");
                    metadata.Add("ContentStreamMimeType", remoteDocument.ContentStreamMimeType);

                    // Create database entry for this file.
                    database.AddFile(filepath, remoteDocument.LastModificationDate, metadata);
                }
                activityListener.ActivityStopped();
            }

            private void CopyStream(Stream src, Stream dst)
            {
                byte[] buffer = new byte[8 * 1024];
                while (true)
                {
                    int read = src.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        return;
                    dst.Write(buffer, 0, read);
                }
            }

            ///**
            // * Download a single file from the CMIS server.
            // */
            //private void DownloadFile(IDocument remoteDocument, string localFolder)
            //{
            //    activityListener.ActivityStarted();

            //    //TODO - Yannick - CanSeek is not supported on contentStream.Stream but we can do the trick with HttpWebRequest.AddRange that is implemented behind GetContentStream(String id, Int64? offset, Int64? length)
            //    //The download code must be rewrite from scratch.
            //    DotCMIS.Data.IContentStream contentStream = remoteDocument.GetContentStream();

            //    // If this file does not have a content stream, ignore it.
            //    // Even 0 bytes files have a contentStream.
            //    // null contentStream sometimes happen on IBM P8 CMIS server, not sure why.
            //    if (contentStream == null)
            //    {
            //        SparkleLogger.LogInfo("Sync", "Skipping download of file with null content stream: " + remoteDocument.ContentStreamFileName);
            //        return;
            //    }

            //    // Download.
            //    string filePath = localFolder + Path.DirectorySeparatorChar + contentStream.FileName;

            //    // If there was previously a directory with this name, delete it.
            //    // TODO warn if local changes inside the folder.
            //    if (Directory.Exists(filePath))
            //    {
            //        Directory.Delete(filePath);
            //    }

            //    bool success = false;
            //    do
            //    {
            //        try
            //        {
            //            DownloadFile(contentStream, filePath);
            //            success = true;
            //        }
            //        catch (WebException e)
            //        {
            //            SparkleLogger.LogInfo("Sync", e.Message);
            //            SparkleLogger.LogInfo("Sync", "Problem during download, waiting for 10 seconds...");
            //            System.Threading.Thread.Sleep(10 * 1000);
            //        }
            //    }
            //    while (!success);

            //    // Get metadata.
            //    Dictionary<string, string> metadata = new Dictionary<string, string>();
            //    metadata.Add("Id", remoteDocument.Id);
            //    metadata.Add("VersionSeriesId", remoteDocument.VersionSeriesId);
            //    metadata.Add("VersionLabel", remoteDocument.VersionLabel);
            //    metadata.Add("CreationDate", remoteDocument.CreationDate.ToString());
            //    metadata.Add("CreatedBy", remoteDocument.CreatedBy);
            //    metadata.Add("lastModifiedBy", remoteDocument.LastModifiedBy);
            //    metadata.Add("CheckinComment", remoteDocument.CheckinComment);
            //    metadata.Add("IsImmutable", (bool)(remoteDocument.IsImmutable) ? "true" : "false");
            //    metadata.Add("ContentStreamMimeType", remoteDocument.ContentStreamMimeType);

            //    // Create database entry for this file.
            //    database.AddFile(filePath, remoteDocument.LastModificationDate, metadata);
            //    activityListener.ActivityStopped();
            //}

            ///**
            // * Download a file, without retrying
            // */
            //private void DownloadFile(DotCMIS.Data.IContentStream contentStream, string filePath)
            //{
            //    SparkleLogger.LogInfo("Sync", "Downloading " + filePath);
            //    // Append .sync at the end of the filename
            //    String tmpfile = filePath + ".sync";
            //    Stream file = File.OpenWrite(tmpfile);
            //    CopyStream(contentStream.Stream, file);
            //    //byte[] buffer = new byte[8 * 1024];
            //    //int len;
            //    //while ((len = contentStream.Stream.Read(buffer, 0, buffer.Length)) > 0) // TODO catch WebException here and retry
            //    //{
            //    //    file.Write(buffer, 0, len);
            //    //}
            //    file.Close();
            //    contentStream.Stream.Close();
            //    File.Move(tmpfile, filePath);
            //    SparkleLogger.LogInfo("Sync", "Downloaded");
            //}

            /**
             * Upload a single file to the CMIS server.
             */
            private void UploadFile(string filePath, IFolder remoteFolder)
            {
                activityListener.ActivityStarted();
                IDocument remoteDocument = null;
                try
                {
                    SparkleLogger.LogInfo("Sync", String.Format("Start upload of file {0}", filePath));

                    // Prepare properties
                    string fileName = Path.GetFileName(filePath);
                    string tmpfileName = fileName + ".sync";
                    Dictionary<string, object> properties = new Dictionary<string, object>();
                    properties.Add(PropertyIds.Name, tmpfileName);
                    properties.Add(PropertyIds.ObjectTypeId, "cmis:document");

                    Boolean success = false;

                    // Prepare content stream
                    Stream file = File.OpenRead(filePath);
                    ContentStream contentStream = new ContentStream();
                    contentStream.FileName = fileName;
                    contentStream.Stream = new MemoryStream();
                    contentStream.MimeType = MimeType.GetMIMEType(fileName); // Should CmisSync try to guess?
                    //contentStream.Length = file.Length;
                    //contentStream.Stream = file;

                    // Upload
                    try
                    {
                        try
                        {
                            string remotepath = remoteFolder.Path + '/' + tmpfileName;
                            ICmisObject obj = session.GetObjectByPath(remotepath);
                            if (obj != null)
                            {
                                SparkleLogger.LogInfo("Sync", "Temp file exist on remote server, so use it");
                                remoteDocument = (IDocument)obj;
                            }
                        }
                        catch (DotCMIS.Exceptions.CmisObjectNotFoundException)
                        {
                            // Create an empty file on remote server and get ContentStream
                            remoteDocument = remoteFolder.CreateDocument(properties, contentStream, null);
                            SparkleLogger.LogInfo("Sync", String.Format("File do not exist on remote server, so create an Empty file on the CMIS Server for {0} and launch a simple update", filePath));
                        }
                        if (remoteDocument == null) return;

                        // This two method have same effect at this time, but first could be helpful when AppendMethod will be available (CMIS1.1)
                        UpdateFile(filePath, remoteDocument);
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        SparkleLogger.LogInfo("Sync", String.Format("Upload of file {0} abort: {1}", filePath, ex));
                        success = false;
                        if (contentStream != null) contentStream.Stream.Close();
                    }

                    if (success)
                    {
                        SparkleLogger.LogInfo("Sync", String.Format("Upload of file {0} finished", filePath));
                        if (contentStream != null) contentStream.Stream.Close();
                        properties[PropertyIds.Name] = fileName;

                        // Object update change ID
                        DotCMIS.Client.IObjectId objID = remoteDocument.UpdateProperties(properties, true);
                        remoteDocument = (IDocument)session.GetObject(objID);

                        // Create database entry for this file.
                        database.AddFile(filePath, remoteDocument.LastModificationDate, null);

                        // Get metadata.
                        Dictionary<string, string> metadata = new Dictionary<string, string>();
                        metadata.Add("Id", remoteDocument.Id);
                        metadata.Add("VersionSeriesId", remoteDocument.VersionSeriesId);
                        metadata.Add("VersionLabel", remoteDocument.VersionLabel);
                        metadata.Add("CreationDate", remoteDocument.CreationDate.ToString());
                        metadata.Add("CreatedBy", remoteDocument.CreatedBy);
                        metadata.Add("lastModifiedBy", remoteDocument.LastModifiedBy);
                        metadata.Add("CheckinComment", remoteDocument.CheckinComment);
                        metadata.Add("IsImmutable", (bool)(remoteDocument.IsImmutable) ? "true" : "false");
                        metadata.Add("ContentStreamMimeType", remoteDocument.ContentStreamMimeType);

                        // Create database entry for this file.
                        database.AddFile(filePath, remoteDocument.LastModificationDate, metadata);
                    }
                }
                catch (Exception e)
                {
                    if (e is FileNotFoundException ||
                        e is IOException)
                    {
                        SparkleLogger.LogInfo("Sync", "File deleted while trying to upload it, reverting.");
                        // File has been deleted while we were trying to upload/checksum/add.
                        // This can typically happen in Windows when creating a new text file and giving it a name.
                        // Revert the upload.
                        if (remoteDocument != null)
                        {
                            remoteDocument.DeleteAllVersions();
                        }
                    }
                    else
                    {
                        throw;
                    }
                }
                activityListener.ActivityStopped();
            }

            /**
             * Upload folder recursively.
             * After execution, the hierarchy on server will be: .../remoteBaseFolder/localFolder/...
             */
            private void UploadFolderRecursively(IFolder remoteBaseFolder, string localFolder)
            {
                // Create remote folder.
                Dictionary<string, object> properties = new Dictionary<string, object>();
                properties.Add(PropertyIds.Name, Path.GetFileName(localFolder));
                properties.Add(PropertyIds.ObjectTypeId, "cmis:folder");
                IFolder folder = remoteBaseFolder.CreateFolder(properties);

                // Create database entry for this folder.
                database.AddFolder(localFolder, folder.LastModificationDate);

                // Upload each file in this folder.
                foreach (string file in Directory.GetFiles(localFolder))
                {
                    UploadFile(file, folder);
                }

                // Recurse for each subfolder in this folder.
                foreach (string subfolder in Directory.GetDirectories(localFolder))
                {
                    UploadFolderRecursively(folder, subfolder);
                }
            }



            private void UpdateFile(string filePath, IDocument remoteFile)
            {
                Stream localfile = File.OpenRead(filePath);
                if (localfile == null)
                {
                    SparkleLogger.LogInfo("Sync", "Skipping upload/update of file with null content stream: " + filePath);
                    throw new IOException();
                }

                // Prepare content stream
                string fileName = Path.GetFileName(filePath);

                ContentStream remoteStream = new ContentStream();
                remoteStream.Stream = localfile;
                remoteStream.Length = localfile.Length;
                remoteStream.MimeType = MimeType.GetMIMEType(fileName);

                // CMIS do not have a Method to upload block by block. So upload file must be full.
                // We must waiting for support of CMIS 1.1 https://issues.apache.org/jira/browse/CMIS-628
                // http://docs.oasis-open.org/cmis/CMIS/v1.1/cs01/CMIS-v1.1-cs01.html#x1-29700019
                DotCMIS.Client.IObjectId objID = remoteFile.SetContentStream(remoteStream, true, true);

                localfile.Close();
                SparkleLogger.LogInfo("Sync", "Update finished:" + filePath);

            }

            /**
             * Upload new version of file content.
             */
            private void UpdateFile(string filePath, IFolder remoteFolder)
            {
                SparkleLogger.LogInfo("Sync", "Updated " + filePath);
                activityListener.ActivityStarted();
                string fileName = Path.GetFileName(filePath);

                IDocument document = null;
                bool found = false;
                foreach (ICmisObject obj in remoteFolder.GetChildren())
                {
                    if (obj is IDocument)
                    {
                        document = (IDocument)obj;
                        if (document.Name == fileName)
                        {
                            found = true;
                            break;
                        }
                    }
                }

                // If not found, it means the document has been deleted, will be processed at the next sync cycle.
                if (!found)
                {
                    SparkleLogger.LogInfo("Sync", filePath + " not found on server, must be uploaded instead of updated");
                    return;
                }

                UpdateFile(filePath, document);

                // Read new last modification date.
                // Update timestamp in database.
                database.SetFileServerSideModificationDate(filePath, document.LastModificationDate);
                activityListener.ActivityStopped();
            }

            /**
             * Remove folder from local filesystem and database.
             */
            private void RemoveFolderLocally(string folderPath)
            {
                // Folder has been deleted on server, delete it locally too.
                SparkleLogger.LogInfo("Sync", "Removing remotely deleted folder: " + folderPath);
                Directory.Delete(folderPath, true);

                // Delete folder from database.
                database.RemoveFolder(folderPath);
            }

            /**
             * Find an available name (potentially suffixed) for this file.
             * For instance:
             * - if /dir/file does not exist, return the same path
             * - if /dir/file exists, return /dir/file (1)
             * - if /dir/file (1) also exists, return /dir/file (2)
             * - etc
             */
            public static string SuffixIfExists(String path)
            {
                if (!File.Exists(path))
                {
                    return path;
                }
                else
                {
                    int index = 1;
                    do
                    {
                        string ret = path + " (" + index + ")";
                        if (!File.Exists(ret))
                        {
                            return ret;
                        }
                        index++;
                    }
                    while (true);
                }
            }

            /**
             * Check if the filename provide is compliance
             * Return true if path is ok, or false is path contains one or more rule
             * */
            public Boolean CheckRules(string path, RulesType ruletype)
            {
                string[] contents = new string[] {
                "~",             // gedit and emacs
                "Thumbs.db", "Desktop.ini","desktop.ini","thumbs.db", // Windows
                "$~"
            };

                string[] extensions = new string[] {
            ".autosave", // Various autosaving apps
            ".~lock", // LibreOffice
            ".part", ".crdownload", // Firefox and Chromium temporary download files
            ".sw[a-z]", ".un~", ".swp", ".swo", // vi(m)
            ".directory", // KDE
            ".DS_Store", ".Icon\r\r", "._", ".Spotlight-V100", ".Trashes", // Mac OS X
            ".(Autosaved).graffle", // Omnigraffle
            ".tmp", ".TMP", // MS Office
            ".~ppt", ".~PPT", ".~pptx", ".~PPTX",
            ".~xls", ".~XLS", ".~xlsx", ".~XLSX",
            ".~doc", ".~DOC", ".~docx", ".~DOCX",
            ".cvsignore", ".~cvsignore", // CVS
            ".sync", // CmisSync File Downloading/Uploading
            ".cmissync" // CmisSync Database 
            };

                string[] directories = new string[] {
                "CVS",".svn",".hg",".bzr",".DS_Store", ".Icon\r\r", "._", ".Spotlight-V100", ".Trashes" // Mac OS X
            };

                SparkleLogger.LogInfo("Sync", "Check rules for " + path);
                Boolean found = false;
                foreach (string content in contents)
                {
                    if (path.Contains(content)) found = true;
                }

                if (ruletype == RulesType.Folder)
                {
                    foreach (string dir in directories)
                    {
                        if (path.Contains(dir)) found = true;
                    }
                }
                else
                {
                    foreach (string ext in extensions)
                    {
                        string filext = Path.GetExtension(path);
                        if (filext == ext) found = true;
                    }
                }

                return !found;

            }
        }
    }

}