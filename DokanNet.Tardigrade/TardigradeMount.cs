﻿using DokanNet.Logging;
using DokanNet.Tardigrade.Interfaces;
using DokanNet.Tardigrade.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;
using uplink.NET.Models;
using uplink.NET.Services;
using static DokanNet.FormatProviders;

namespace DokanNet.Tardigrade
{
    public class TardigradeMount : ITardigradeMount, IDokanOperations
    {
        /// <summary>
        /// Defines the root-folder given from the OS
        /// </summary>
        const string ROOT_FOLDER = "\\";

        /// <summary>
        /// Objects with this name are (probably) empty folders. As Storj cannot create
        /// prefixes out of nowhere, we use this file to fake a folder on the network.
        /// It gets transferred into a Prefix in ListAllAsync().
        /// </summary>
        const string DOKAN_FOLDER = "/folder.dokan";

        /// <summary>
        /// The MemoryCache-Entry-Name for the result of ListAllAsync().
        /// </summary>
        const string LIST_CACHE = "LIST";

        /// <summary>
        /// A combined field to know the access-type in CreateFile
        /// </summary>
        private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                              FileAccess.Execute |
                                              FileAccess.GenericExecute | FileAccess.GenericWrite |
                                              FileAccess.GenericRead;

        /// <summary>
        /// A combined field to know the access-type in CreateFile
        /// </summary>
        private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData |
                                                   FileAccess.Delete |
                                                   FileAccess.GenericWrite;

        /// <summary>
        /// The access to the storj-network
        /// </summary>
        private Access _access;

        /// <summary>
        /// The BucketService to create/access the bucket
        /// </summary>
        private BucketService _bucketService;

        /// <summary>
        /// The ObjectService to upload, list, download and delete objects within a bucket
        /// </summary>
        private ObjectService _objectService;

        /// <summary>
        /// The bucket used for this mount
        /// </summary>
        private Bucket _bucket;

        /// <summary>
        /// The logger used in DEBUG-mode
        /// </summary>
        private ConsoleLogger logger = new ConsoleLogger("[Tardigrade] ");

        /// <summary>
        /// The MemoryCache holds mainly the result of ListAllAsync plus some accessed files.
        /// It reduces the amount of data retrieved from the network lowering costs, saving bandwith and getting overall better performance.
        /// </summary>
        private ObjectCache _memoryCache = MemoryCache.Default;

        /// <summary>
        /// The dictionary maps a filename to it's currently running upload. IDokanFile.Context was used here before, but that
        /// sometimes does not really keep track of that Upload leading to errors on file transfer. Therefore the mapping is hold
        /// seperately from Dokan.
        /// </summary>
        private Dictionary<string, ChunkedUploadOperation> _currentUploads = new Dictionary<string, ChunkedUploadOperation>();

        #region Implementation of ITardigradeMount

        /// <summary>
        /// Mounts a given bucket with given access privileges. This method will block until the drive gets unmounted again. TODO
        /// </summary>
        /// <param name="mountParameters">The parameters to use for a  mount</param>
        /// <returns>A task doing the initialization</returns>
        public async Task MountAsync(MountParameters mountParameters)
        {
            Access.SetTempDirectory(System.IO.Path.GetTempPath());

            if (string.IsNullOrEmpty(mountParameters.AccessGrant))
                _access = new Access(mountParameters.SatelliteAddress, mountParameters.ApiKey, mountParameters.EncryptionPassphrase);
            else
                _access = new Access(mountParameters.AccessGrant);

            await InitUplinkAsync(mountParameters.Bucketname);

            this.Mount(mountParameters.DriveLetter.ToString() + ":\\", DokanOptions.DebugMode, 1);
        }
        #endregion

        #region uplink-Access
        private async Task InitUplinkAsync(string bucketName)
        {
            _bucketService = new BucketService(_access);
            _objectService = new ObjectService(_access);
            _bucket = await _bucketService.EnsureBucketAsync(bucketName);
        }

        private async Task<List<uplink.NET.Models.Object>> ListAllAsync()
        {
            var result = _memoryCache[LIST_CACHE] as List<uplink.NET.Models.Object>;
            if (result == null)
            {
                var objects = await _objectService.ListObjectsAsync(_bucket, new ListObjectsOptions() { Recursive = true, Custom = true, System = true });
                result = new List<uplink.NET.Models.Object>();
                foreach (var obj in objects.Items)
                {
                    if(obj.Key.Contains(DOKAN_FOLDER))
                    {
                        obj.IsPrefix = true;
                        obj.Key = obj.Key.Replace(DOKAN_FOLDER, "");
                    }
                    result.Add(obj);
                }

                var cachePolicy = new CacheItemPolicy();
                cachePolicy.AbsoluteExpiration = DateTimeOffset.Now.AddMinutes(1);
                _memoryCache.Set(LIST_CACHE, result, cachePolicy);
            }

            return result;
        }
        #endregion

        #region Helper

        private string ToInternalFolder(string fileName)
        {
            return fileName + "/" + DOKAN_FOLDER;
        }

        private void ClearMemoryCache(string fileName = null)
        {
            _memoryCache.Remove(LIST_CACHE);

            if (fileName != null)
                _memoryCache.Remove(fileName);
        }
        protected NtStatus Trace(string method, string fileName, IDokanFileInfo info, NtStatus result,
            params object[] parameters)
        {
#if TRACE
            var extraParameters = parameters != null && parameters.Length > 0
                ? ", " + string.Join(", ", parameters.Select(x => string.Format(DefaultFormatProvider, "{0}", x)))
                : string.Empty;

            logger.Debug(DokanFormat($"{method}('{fileName}', {info}{extraParameters}) -> {result}"));
#endif

            return result;
        }

        private NtStatus Trace(string method, string fileName, IDokanFileInfo info,
            FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes,
            NtStatus result)
        {
#if TRACE
            logger.Debug(
                DokanFormat(
                    $"{method}('{fileName}', {info}, [{access}], [{share}], [{mode}], [{options}], [{attributes}]) -> {result}"));
            if (result == NtStatus.ObjectNameNotFound)
            {

            }
#endif

            return result;
        }

        protected string GetPath(string fileName)
        {
            if (fileName == ROOT_FOLDER)
                return fileName;
            else
            {
                if (fileName.StartsWith(ROOT_FOLDER))
                    return fileName.Substring(1);
                else
                    return fileName;
            }
        }

        public IList<FileInformation> FindFilesHelper(string fileName, string searchPattern)
        {
            var listTask = ListAllAsync();
            listTask.Wait();

            var currentFolder = fileName.Substring(1);
            if (currentFolder != "")
                currentFolder = currentFolder + "\\";

            IList<FileInformation> files;
            IList<FileInformation> folders;

            if (currentFolder == "")
            {
                //In the root-folder
                files = listTask.Result
                .Where(finfo => finfo.Key.StartsWith(currentFolder) && !finfo.Key.Contains("\\") &&
                                DokanHelper.DokanIsNameInExpression(searchPattern, finfo.Key, true) &&
                                !finfo.IsPrefix)
                .Select(finfo => new FileInformation
                {
                    Attributes = FileAttributes.Normal,
                    CreationTime = finfo.SystemMetaData.Created,
                    LastAccessTime = finfo.SystemMetaData.Created,
                    LastWriteTime = finfo.SystemMetaData.Created,
                    Length = finfo.SystemMetaData.ContentLength,
                    FileName = finfo.Key
                }).ToArray();
                folders = listTask.Result
                    .Where(finfo => finfo.Key.StartsWith(currentFolder) && !finfo.Key.Contains("\\") &&
                                    DokanHelper.DokanIsNameInExpression(searchPattern, finfo.Key, true) &&
                                    finfo.IsPrefix)
                    .Select(finfo => new FileInformation
                    {
                        Attributes = FileAttributes.Directory,
                        CreationTime = finfo.SystemMetaData.Created,
                        LastAccessTime = finfo.SystemMetaData.Created,
                        LastWriteTime = finfo.SystemMetaData.Created,
                        Length = 0,
                        FileName = finfo.Key
                    }).ToArray();
            }
            else
            {
                //In any subfolder
                files = listTask.Result
                .Where(finfo => finfo.Key.StartsWith(currentFolder) && !finfo.Key.Substring(currentFolder.Length).Contains("\\") &&
                                DokanHelper.DokanIsNameInExpression(searchPattern, finfo.Key, true) &&
                                !finfo.IsPrefix)
                .Select(finfo => new FileInformation
                {
                    Attributes = FileAttributes.Normal,
                    CreationTime = finfo.SystemMetaData.Created,
                    LastAccessTime = finfo.SystemMetaData.Created,
                    LastWriteTime = finfo.SystemMetaData.Created,
                    Length = finfo.SystemMetaData.ContentLength,
                    FileName = finfo.Key.Substring(currentFolder.Length)
                }).ToArray();
                folders = listTask.Result
                    .Where(finfo => finfo.Key.StartsWith(currentFolder) &&
                                    DokanHelper.DokanIsNameInExpression(searchPattern, finfo.Key, true) &&
                                    finfo.IsPrefix)
                    .Select(finfo => new FileInformation
                    {
                        Attributes = FileAttributes.Directory,
                        CreationTime = finfo.SystemMetaData.Created,
                        LastAccessTime = finfo.SystemMetaData.Created,
                        LastWriteTime = finfo.SystemMetaData.Created,
                        Length = 0,
                        FileName = finfo.Key.Substring(currentFolder.Length)
                    }).ToArray();
            }

            List<FileInformation> result = new List<FileInformation>();
            foreach (var folder in folders)
                result.Add(folder);
            foreach (var file in files)
                result.Add(file);

            return result;
        }

        private void InitDownload(string fileName, IDokanFileInfo info)
        {
            if (info.Context == null && _memoryCache[fileName] != null)
                info.Context = _memoryCache[fileName];

            if (info.Context == null)
            {
                var realFileName = GetPath(fileName);

                var getObjectTask = _objectService.GetObjectAsync(_bucket, realFileName);
                getObjectTask.Wait();
                info.Context = new DownloadStream(_bucket, (int)getObjectTask.Result.SystemMetaData.ContentLength, realFileName, _access);
                var cachePolicy = new CacheItemPolicy();
                cachePolicy.AbsoluteExpiration = DateTimeOffset.Now.AddMinutes(30);
                _memoryCache.Set(fileName, info.Context, cachePolicy);
            }
        }
        private void CleanupDownload(IDokanFileInfo info)
        {
            return;
        }

        private void InitChunkedUpload(string fileName, IDokanFileInfo info)
        {
            if (!_currentUploads.ContainsKey(fileName))
            {
                var realFileName = GetPath(fileName);

                var uploadTask = _objectService.UploadObjectChunkedAsync(_bucket, realFileName, new UploadOptions(), null);
                uploadTask.Wait();
                _currentUploads.Add(fileName, uploadTask.Result);
            }
        }
        private void CleanupChunkedUpload(string fileName, IDokanFileInfo info)
        {
            if (_currentUploads.ContainsKey(fileName))
            {
                var commitResult = _currentUploads[fileName].Commit();
                _currentUploads.Remove(fileName);
                ClearMemoryCache(fileName);
            }
        }
        #endregion

        #region Implementation of IDokanOperations
        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
        {
            freeBytesAvailable = 512 * 1024 * 1024;
            totalNumberOfBytes = 1024 * 1024 * 1024;
            totalNumberOfFreeBytes = 512 * 1024 * 1024;
            return Trace(nameof(GetDiskFreeSpace), null, info, DokanResult.Success, "out " + freeBytesAvailable.ToString(),
                "out " + totalNumberOfBytes.ToString(), "out " + totalNumberOfFreeBytes.ToString());
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            // This function is not called because FindFilesWithPattern is implemented
            // Return DokanResult.NotImplemented in FindFilesWithPattern to make FindFiles called
            files = FindFilesHelper(fileName, "*");

            return Trace(nameof(FindFiles), fileName, info, DokanResult.Success);
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
        {
            files = FindFilesHelper(fileName, searchPattern);

            return Trace(nameof(FindFilesWithPattern), fileName, info, DokanResult.Success);
        }
        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
        {
            volumeLabel = "Tardigrade";
            fileSystemName = "NTFS";
            maximumComponentLength = 256;

            features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.CaseSensitiveSearch |
                       FileSystemFeatures.PersistentAcls | FileSystemFeatures.SupportsRemoteStorage |
                       FileSystemFeatures.UnicodeOnDisk;

            return Trace(nameof(GetVolumeInformation), null, info, DokanResult.Success, "out " + volumeLabel,
                "out " + features.ToString(), "out " + fileSystemName);
        }

        public NtStatus Mounted(IDokanFileInfo info)
        {
            return Trace(nameof(Mounted), null, info, DokanResult.Success);
        }

        public NtStatus Unmounted(IDokanFileInfo info)
        {
            return Trace(nameof(Unmounted), null, info, DokanResult.Success);
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            security = null;
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            return DokanResult.Error;
        }

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            //Creates an empty file
            var file = GetPath(fileName);
            var uploadTask = _objectService.UploadObjectAsync(_bucket, file, new UploadOptions(), new byte[] { }, false);
            uploadTask.Wait();
            var result = uploadTask.Result;
            result.StartUploadAsync().Wait();
            ClearMemoryCache();
            return Trace(nameof(SetAllocationSize), fileName, info, DokanResult.Success);
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            InitChunkedUpload(fileName, info);

            ClearMemoryCache();
            return Trace(nameof(SetEndOfFile), fileName, info, DokanResult.Success);
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            if (!_currentUploads.ContainsKey(fileName))
                InitChunkedUpload(fileName, info);

            var chunkedUpload = _currentUploads[fileName];
            var chunkUploaded = chunkedUpload.WriteBytes(buffer);
            if (chunkUploaded)
            {
                bytesWritten = buffer.Length;
                return Trace(nameof(WriteFile), fileName, info, DokanResult.Success);
            }
            else
            {
                bytesWritten = 0;
                return Trace(nameof(WriteFile), fileName, info, DokanResult.Error);
            }
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
        {
            InitDownload(fileName, info);

            var download = info.Context as DownloadStream;
            download.Position = offset;
            if (download.Length > 0 && download.Length < offset + buffer.Length)
                bytesRead = download.Read(buffer, 0, (int)(download.Length - offset));
            else
                bytesRead = download.Read(buffer, 0, buffer.Length);
            return Trace(nameof(ReadFile), fileName, info, DokanResult.Success);
        }

        public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            var moveFileTask = MoveFileAsync(oldName, newName, replace, info);
            moveFileTask.Wait();
            return moveFileTask.Result;
        }

        public async Task<NtStatus> MoveFileAsync(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            var realOldName = GetPath(oldName);
            var realNewName = GetPath(newName);

            if(info.IsDirectory)
            {
                //The Directory has to be empty - otherwise we would have to copy every object with that path.
                //Furthermore we need to use the "folder.dokan"-file for the "rename".
                var files = await ListAllAsync();
                if(files.Where(f=>!f.IsPrefix && f.Key.StartsWith(realOldName)).Count() > 0)
                {
                    return DokanResult.DirectoryNotEmpty;
                }

                realOldName = ToInternalFolder(realOldName);
                realNewName = ToInternalFolder(realNewName);
            }

            if (replace)
                await _objectService.DeleteObjectAsync(_bucket, realNewName);

            var download = await _objectService.DownloadObjectAsync(_bucket, realOldName, new DownloadOptions(), false);
            await download.StartDownloadAsync();

            var upload = await _objectService.UploadObjectAsync(_bucket, realNewName, new UploadOptions(), download.DownloadedBytes, false);
            await upload.StartUploadAsync();

            await _objectService.DeleteObjectAsync(_bucket, realOldName);

            ClearMemoryCache();

            return DokanResult.Success;
        }

        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            //The real delete happens on Cleanup
            ClearMemoryCache();

            return Trace(nameof(DeleteFile), fileName, info, DokanResult.Success);
        }

        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            var result = DokanResult.Success;
            var filePath = GetPath(fileName);

            if (info.IsDirectory)
            {
                try
                {
                    switch (mode)
                    {
                        case FileMode.Open:
                            //Nothing to do here
                            info.Context = new object();
                            break;

                        case FileMode.CreateNew:
                            //Need to think about how to create a folder with a prefix
                            info.Context = new object();
                            CreateFolder(fileName);
                            break;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.AccessDenied);
                }
            }
            else //IsFile
            {
                var pathExists = true;
                var pathIsDirectory = false;

                var readWriteAttributes = (access & DataAccess) == 0;
                var readAccess = (access & DataWriteAccess) == 0;
                var listTask = ListAllAsync();
                listTask.Wait();

                var prefix = listTask.Result.Where(l => l.Key == filePath).FirstOrDefault();
                pathExists = prefix != null ? true : false;
                pathIsDirectory = prefix != null && prefix.IsPrefix ? true : false;

                switch (mode)
                {
                    case FileMode.Open:

                        if (pathExists || fileName == ROOT_FOLDER)
                        {
                            // check if driver only wants to read attributes, security info, or open directory
                            if (readWriteAttributes || pathIsDirectory)
                            {
                                if (pathIsDirectory && (access & FileAccess.Delete) == FileAccess.Delete
                                    && (access & FileAccess.Synchronize) != FileAccess.Synchronize)
                                    //It is a DeleteFile request on a directory
                                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                        attributes, DokanResult.AccessDenied);

                                info.IsDirectory = pathIsDirectory;

                                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.Success);
                            }
                        }
                        else
                        {
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileNotFound);
                        }
                        break;

                    case FileMode.CreateNew:
                        if (pathExists)
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileExists);
                        InitChunkedUpload(fileName, info);
                        break;

                    case FileMode.Truncate:
                        if (!pathExists)
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileNotFound);
                        break;
                }

                if (pathExists && (mode == FileMode.OpenOrCreate || mode == FileMode.Create))
                    result = DokanResult.AlreadyExists;
            }
            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                result);
        }

        private void CreateFolder(string folderName)
        {
            var file = GetPath(folderName);
            file = ToInternalFolder(file);
            var uploadTask = _objectService.UploadObjectAsync(_bucket, file, new UploadOptions(), new byte[] { }, false);
            uploadTask.Wait();
            var result = uploadTask.Result;
            result.StartUploadAsync().Wait();
            ClearMemoryCache();
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            if (_currentUploads.ContainsKey(fileName))
            {
                fileInfo = new FileInformation
                {
                    FileName = fileName,
                    Attributes = FileAttributes.NotContentIndexed | FileAttributes.Archive,
                    CreationTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    Length = 0,
                };
            }
            else if (fileName == ROOT_FOLDER || info.IsDirectory)
            {
                fileInfo = new FileInformation
                {
                    FileName = fileName,
                    Attributes = FileAttributes.Directory,
                    CreationTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    Length = 0,
                };
            }
            else
            {
                var filePath = GetPath(fileName);

                var listTask = ListAllAsync();
                listTask.Wait();

                var file = listTask.Result.Where(l => l.Key == filePath).FirstOrDefault();
                var fileExists = file != null ? true : false;
                if (!fileExists)
                {
                    fileInfo = new FileInformation();
                    return Trace(nameof(GetFileInformation), fileName, info, DokanResult.FileNotFound);
                }

                if (file.IsPrefix)
                {
                    //See it as a directory
                    fileInfo = new FileInformation
                    {
                        FileName = fileName,
                        Attributes = FileAttributes.Directory,
                        CreationTime = file.SystemMetaData.Created,
                        LastAccessTime = file.SystemMetaData.Created, //Todo: use custom meta
                        LastWriteTime = file.SystemMetaData.Created, //Todo: use custom meta
                        Length = 0,
                    };
                }
                else
                {
                    //See it as a file
                    fileInfo = new FileInformation
                    {
                        FileName = fileName,
                        Attributes = FileAttributes.NotContentIndexed | FileAttributes.Archive,
                        CreationTime = file.SystemMetaData.Created,
                        LastAccessTime = file.SystemMetaData.Created, //Todo: use custom meta
                        LastWriteTime = file.SystemMetaData.Created, //Todo: use custom meta
                        Length = file.SystemMetaData.ContentLength,
                    };
                }
            }
            return Trace(nameof(GetFileInformation), fileName, info, DokanResult.Success);
        }

        public void Cleanup(string fileName, IDokanFileInfo info)
        {
            CleanupChunkedUpload(fileName, info);
            CleanupDownload(info);

            if (info.DeleteOnClose &&
                !info.IsDirectory) //Directories are deleted with DeleteDirectory
            {
                var realFileName = GetPath(fileName);
                var deleteTask = _objectService.DeleteObjectAsync(_bucket, realFileName);
                deleteTask.Wait();

                ClearMemoryCache(fileName);
            }
        }

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
            CleanupChunkedUpload(fileName, info);
            CleanupDownload(info);
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return Trace(nameof(UnlockFile), fileName, info, DokanResult.NotImplemented);
        }

        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return Trace(nameof(LockFile), fileName, info, DokanResult.NotImplemented);
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            streams = new FileInformation[0];
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented);
        }

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.NotImplemented);
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            var deleteDirectoryTask = DeleteDirectoryAsync(fileName, info);
            deleteDirectoryTask.Wait();
            return deleteDirectoryTask.Result;
        }

        public async Task<NtStatus> DeleteDirectoryAsync(string fileName, IDokanFileInfo info)
        {
            var realFileName = GetPath(fileName);

            //The Directory has to be empty - otherwise we would have to copy every object with that path.
            //Furthermore we need to use the "folder.dokan"-file for the "rename".
            var files = await ListAllAsync();
            foreach(var toDelete in files.Where(f => !f.IsPrefix && f.Key.StartsWith(realFileName)))
            {
                await _objectService.DeleteObjectAsync(_bucket, toDelete.Key);
            }

            realFileName = ToInternalFolder(realFileName);

            await _objectService.DeleteObjectAsync(_bucket, realFileName);

            ClearMemoryCache();

            return Trace(nameof(DeleteDirectoryAsync), fileName, info, DokanResult.Success);
        }
        #endregion
    }
}
