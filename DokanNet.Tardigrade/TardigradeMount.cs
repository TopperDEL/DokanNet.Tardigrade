﻿using DokanNet.Logging;
using DokanNet.Tardigrade.Interfaces;
using System;
using System.Collections.Generic;
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
        const string ROOT_FOLDER = "\\";
        private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                              FileAccess.Execute |
                                              FileAccess.GenericExecute | FileAccess.GenericWrite |
                                              FileAccess.GenericRead;

        private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData |
                                                   FileAccess.Delete |
                                                   FileAccess.GenericWrite;

        private Access _access;
        private BucketService _bucketService;
        private ObjectService _objectService;
        private Bucket _bucket;

        private ConsoleLogger logger = new ConsoleLogger("[Tardigrade] ");

        private ObjectCache _listingCache = MemoryCache.Default;

        #region Implementation of ITardigradeMount
        public async Task MountAsync(string satelliteAddress, string apiKey, string secret, string bucketName)
        {
            Access.SetTempDirectory(System.IO.Path.GetTempPath());
            _access = new Access(satelliteAddress, apiKey, secret);
            await InitUplinkAsync(bucketName);

            Mount();
        }

        public async Task MountAsync(string accessGrant, string bucketName)
        {
            Access.SetTempDirectory(System.IO.Path.GetTempPath());
            _access = new Access(accessGrant);
            await InitUplinkAsync(bucketName);

            Mount();
        }

        private void Mount()
        {
            this.Mount("s:\\", DokanOptions.DebugMode, 1);
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
            var result = _listingCache["all"] as List<uplink.NET.Models.Object>;
            if (result == null)
            {
                var objects = await _objectService.ListObjectsAsync(_bucket, new ListObjectsOptions() { Recursive = true, Custom = true, System = true });
                result = new List<uplink.NET.Models.Object>();
                foreach (var obj in objects.Items)
                {
                    result.Add(obj);
                }

                var cachePolicy = new CacheItemPolicy();
                cachePolicy.AbsoluteExpiration = DateTimeOffset.Now.AddMinutes(1);
                _listingCache.Set("all", result, cachePolicy);
            }

            return result;
        }
        #endregion

        #region Helper

        private void ClearListCache()
        {
            _listingCache.Remove("all");
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

            IList<FileInformation> files = listTask.Result
                .Where(finfo => DokanHelper.DokanIsNameInExpression(searchPattern, finfo.Key, true))
                .Select(finfo => new FileInformation
                {
                    Attributes = FileAttributes.Normal,
                    CreationTime = finfo.SystemMetaData.Created,
                    LastAccessTime = finfo.SystemMetaData.Created,
                    LastWriteTime = finfo.SystemMetaData.Created,
                    Length = finfo.SystemMetaData.ContentLength,
                    FileName = finfo.Key
                }).ToArray();

            return files;
        }
        #endregion

        #region Done
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
        #endregion

        #region partly implemented / not sure
        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            security = null;
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            return DokanResult.Error;
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
                                info.Context = prefix; // must set it to someting if you return DokanError.Success

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
                        info.Context = "NEW_UPLOAD";
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

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            if (info.Context != null && info.Context == "NEW_UPLOAD")
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
            else if (fileName == ROOT_FOLDER)
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
        #endregion

        #region To implement

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            //Creates an empty file
            var file = GetPath(fileName);
            var uploadTask = _objectService.UploadObjectAsync(_bucket, file, new UploadOptions(), new byte[] { }, false);
            uploadTask.Wait();
            var result = uploadTask.Result;
            result.StartUploadAsync().Wait();
            ClearListCache();
            return DokanResult.Success;
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            ClearListCache();
            return DokanResult.Success;
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            var writeFileTask = WriteFileAsync(fileName, buffer, offset,info);
            writeFileTask.Wait();
            bytesWritten = writeFileTask.Result.Item2;
            return writeFileTask.Result.Item1;
        }

        public async Task<Tuple<NtStatus,int>> WriteFileAsync(string fileName, byte[] buffer, long offset, IDokanFileInfo info)
        {
            var realFileName = GetPath(fileName);

            var upload = await _objectService.UploadObjectAsync(_bucket, realFileName, new UploadOptions(), buffer, false);
            await upload.StartUploadAsync();

            ClearListCache();

            if (upload.Completed)
            {
                return new Tuple<NtStatus, int>(DokanResult.Success, (int)upload.BytesSent);
            }
            else
            {
                return new Tuple<NtStatus, int>(DokanResult.Error, 0);
            }
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
        {
            var readFileTask = ReadFileAsync(fileName, buffer, offset, info);
            readFileTask.Wait();
            bytesRead = readFileTask.Result.Item2;
            return readFileTask.Result.Item1;
        }
        public async Task<Tuple<NtStatus, int>> ReadFileAsync(string fileName, byte[] buffer, long offset, IDokanFileInfo info)
        {
            var realFileName = GetPath(fileName);

            var download = await _objectService.DownloadObjectAsync(_bucket, realFileName, new DownloadOptions(), false);
            await download.StartDownloadAsync();

            Array.Copy(download.DownloadedBytes, buffer, download.BytesReceived);

            return new Tuple<NtStatus, int>(DokanResult.Success, (int)download.BytesReceived);
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

            if (replace)
                await _objectService.DeleteObjectAsync(_bucket, realNewName);

            var download = await _objectService.DownloadObjectAsync(_bucket, realOldName, new DownloadOptions(), false);
            await download.StartDownloadAsync();

            var upload = await _objectService.UploadObjectAsync(_bucket, realNewName, new UploadOptions(), download.DownloadedBytes, false);
            await upload.StartUploadAsync();

            await _objectService.DeleteObjectAsync(_bucket, realOldName);

            ClearListCache();

            return DokanResult.Success;
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        {
            return DokanResult.Success;
            throw new NotImplementedException();
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            throw new NotImplementedException();
        }
        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            throw new NotImplementedException();
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            return DokanResult.Success;
            throw new NotImplementedException();
        }

        public void Cleanup(string fileName, IDokanFileInfo info)
        {
        }

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            throw new NotImplementedException();
        }

        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            throw new NotImplementedException();
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            throw new NotImplementedException();
        }

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            throw new NotImplementedException();
        }



        #endregion
    }
}
