﻿using Raven.Abstractions.Data;
using Raven.Abstractions.FileSystem;
using Raven.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Raven.Client.FileSystem
{
    public class AsyncFilesSession : InMemoryFilesSessionOperations, IAsyncFilesSession, IAsyncAdvancedFilesSessionOperations
    {
        	/// <summary>
		/// Initializes a new instance of the <see cref="AsyncFilesSession"/> class.
		/// </summary>
        public AsyncFilesSession(FilesStore filesStore,
                                 IAsyncFilesCommands asyncFilesCommands,
								 FilesSessionListeners listeners,
								 Guid id)
			: base(filesStore, listeners, id)
		{
            Commands = asyncFilesCommands;
		}

        /// <summary>
        /// Gets the async files commands.
        /// </summary>
        /// <value>The async files commands.</value>
        public IAsyncFilesCommands Commands { get; private set; }

        public override string FileSystemName
        {
            get { return Commands.FileSystem; }
        }

        public IAsyncAdvancedFilesSessionOperations Advanced
        {
            get { return this; }
        }

        public async Task<FileHeader> LoadFileAsync(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentNullException("filename", "The filename cannot be null, empty or whitespace.");

            object existingEntity;
            if (entitiesByKey.TryGetValue(filename, out existingEntity))
            {
                return existingEntity as FileHeader;
            }

            IncrementRequestCount();

            var metadata = await Commands.GetMetadataForAsync(filename);
            var fileHeader = new FileHeader(filename, metadata);

            entitiesByKey.Add(filename, fileHeader);

            return fileHeader;
        }

        public Task<FileHeader> LoadFileAsync(DirectoryHeader directory, string filename)
        {
            if (directory == null)
                throw new ArgumentNullException("directory", "The directory cannot be null.");

            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentNullException("filename", "The filename cannot be null, empty or whitespace.");

            return LoadFileAsync(Path.Combine(directory.Path, filename));            
        }

        public async Task<FileHeader[]> LoadFileAsync(IEnumerable<string> filenames)
        {
            if (!filenames.Any())
                return new FileHeader[0];

            // only load documents that aren't already cached
            var idsOfNotExistingObjects = filenames.Where(x => IsLoaded(x) == false && IsDeleted(x) == false)
                                            .Distinct(StringComparer.OrdinalIgnoreCase)
                                            .ToArray();

            if (idsOfNotExistingObjects.Length > 0)
            {
                IncrementRequestCount();

                var fileHeaders = await Commands.GetAsync(idsOfNotExistingObjects.ToArray());                                
                foreach( var header in fileHeaders )
                    entitiesByKey.Add(header.Name, header);                
            }

            var result = new List<FileHeader>();
            foreach ( var file in filenames )
            {
                object obj = null;
                entitiesByKey.TryGetValue(file, out obj);
                result.Add( obj as FileHeader );
            }
            return result.ToArray();
        }

        public Task<DirectoryHeader> LoadDirectoryAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException("path", "The path cannot be null, empty or whitespace.");

            throw new NotImplementedException();
        }

        public Task<Stream> DownloadAsync(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentNullException("filename", "The filename cannot be null, empty or whitespace.");

            throw new NotImplementedException();
        }

        public Task<Stream> DownloadAsync(FileHeader filename)
        {
            throw new NotImplementedException();
        }

        public Task<FileHeader[]> LoadFilesAtDirectoryAsync(DirectoryHeader directory)
        {
            if (directory == null)
                throw new ArgumentNullException("directory", "The directory cannot be null.");

            throw new NotImplementedException();
        }

        public Task<FileHeader[]> LoadFilesAtDirectoryAsync(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentNullException("directory", "The directory cannot be null, empty or whitespace.");

            throw new NotImplementedException();
        }
    }
}