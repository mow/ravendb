using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Abstractions.MEF;
using Raven.Bundles.Compression.Plugin;
using Raven.Bundles.Encryption.Plugin;
using Raven.Bundles.Encryption.Settings;
using Raven.Database.Config;
using Raven.Database.Plugins;
using Raven.Database.Storage;
using Raven.Imports.Newtonsoft.Json;
using Raven.Json.Linq;
using Raven.Database.Util;

namespace Raven.StorageExporter
{
    public class StorageExporter
    {
        public StorageExporter(string databaseBaseDirectory, string databaseOutputFile, 
            int batchSize, Etag documentsStartEtag, bool hasCompression, EncryptionConfiguration encryption)
        {
            HasCompression = hasCompression;
            Encryption = encryption;
            baseDirectory = databaseBaseDirectory;
            outputDirectory = databaseOutputFile;
            var ravenConfiguration = new RavenConfiguration
            {
                DataDirectory = databaseBaseDirectory,
                Storage =
                {
                    PreventSchemaUpdate = true,
                    SkipConsistencyCheck = true
                }
            };
            CreateTransactionalStorage(ravenConfiguration);
            this.batchSize = batchSize;
            DocumentsStartEtag = documentsStartEtag;
        }

        public Etag DocumentsStartEtag { get; set; }

        public bool HasCompression { get; set; }

        public EncryptionConfiguration Encryption { get; set; }

        public void ExportDatabase()
        {
           
            using (var stream = File.Create(outputDirectory))
            using (var gZipStream = new GZipStream(stream, CompressionMode.Compress,leaveOpen: true))
            using (var streamWriter = new StreamWriter(gZipStream))
            {
                var jsonWriter = new JsonTextWriter(streamWriter)
                {
                    Formatting = Formatting.Indented
                };
                jsonWriter.WriteStartObject();
                //Indexes
                jsonWriter.WritePropertyName("Indexes");
                jsonWriter.WriteStartArray();
                WriteIndexes(jsonWriter);
                jsonWriter.WriteEndArray();
                //documents
                jsonWriter.WritePropertyName("Docs");
                jsonWriter.WriteStartArray();
                WriteDocuments(jsonWriter);
                jsonWriter.WriteEndArray();
                //Transformers
                jsonWriter.WritePropertyName("Transformers");
                jsonWriter.WriteStartArray();
                WriteTransformers(jsonWriter);
                jsonWriter.WriteEndArray();
                //Identities
                jsonWriter.WritePropertyName("Identities");
                jsonWriter.WriteStartArray();
                WriteIdentities(jsonWriter);
                jsonWriter.WriteEndArray();
                //end of export
                jsonWriter.WriteEndObject();
                streamWriter.Flush();
            }
        }

        private void ReportProgress(string stage, long from, long outof)
        {
            if (from == outof)
            {
                ConsoleUtils.ConsoleWriteLineWithColor(ConsoleColor.Green, "Completed exporting {0} out of {1} {2}", from, outof, stage);
            }
            else
            {
                Console.WriteLine("exporting {0} out of {1} {2}", from, outof, stage);
            }
        }

        private static void ReportCorruptedDocument(string stage, long currentDocsCount, string error)
        {
            ConsoleUtils.ConsoleWriteLineWithColor(ConsoleColor.Red,
                "Failed to export {0}: document number {1} failed to export, skipping it, error: {2}", 
                stage, currentDocsCount, error);
        }

        private static void ReportCorruptedDocumentWithEtag(string stage, long currentDocsCount, string error, Etag currentLastEtag)
        {
            ConsoleUtils.ConsoleWriteLineWithColor(ConsoleColor.Red,
                "Failed to export {0}: document number {1} with etag {2} failed to export, skipping it, error: {3}",
                stage, currentDocsCount, currentLastEtag, error);
        }

        private void WriteDocuments(JsonTextWriter jsonWriter)
        {
            long totalDocsCount = 0;
            

            storage.Batch(accsesor => totalDocsCount = accsesor.Documents.GetDocumentsCount());

            if (DocumentsStartEtag == Etag.Empty)
            {
                ExtractDocuments(jsonWriter, totalDocsCount);
            }
            else
            {
                ExtractDocumentsFromEtag(jsonWriter, totalDocsCount);
            }
        }

        private void ExtractDocuments(JsonTextWriter jsonWriter, long totalDocsCount)
        {
            long currentDocsCount = 0;
            do
            {
                var previousDocsCount = currentDocsCount;

                try
                {
                    storage.Batch(accsesor =>
                    {
                        var docs = accsesor.Documents.GetDocuments(start: (int) currentDocsCount);
                        foreach (var doc in docs)
                        {
                            doc.ToJson(true).WriteTo(jsonWriter);
                            currentDocsCount++;

                            if (currentDocsCount % batchSize == 0)
                                ReportProgress("documents", currentDocsCount, totalDocsCount);
                        }
                    });
                }
                catch (Exception e)
                {
                    currentDocsCount++;
                    ReportCorruptedDocument("documents", currentDocsCount, e.Message);
                }
                finally
                {
                    if (currentDocsCount > previousDocsCount)
                        ReportProgress("documents", currentDocsCount, totalDocsCount);
                }
            } while (currentDocsCount < totalDocsCount);
        }

        private void ExtractDocumentsFromEtag(JsonTextWriter jsonWriter, long totalDocsCount)
        {
            var currentLastEtag = DocumentsStartEtag;

            Debug.Assert(DocumentsStartEtag != Etag.Empty);
            ConsoleUtils.ConsoleWriteLineWithColor(ConsoleColor.Yellow, "Starting to export documents as of etag={0}\n" +
                    "TotalDocCount doesn't substract skipped items\n", DocumentsStartEtag);
            currentLastEtag.DecrementBy(1);

            var ct = new CancellationToken();
            long currentDocsCount = 0;
            do
            {
                var previousDocsCount = currentDocsCount;
                try
                {
                    storage.Batch(accsesor =>
                    {
                        var docs = accsesor.Documents.GetDocumentsAfter(currentLastEtag, batchSize, ct);
                        foreach (var doc in docs)
                        {
                            doc.ToJson(true).WriteTo(jsonWriter);
                            currentDocsCount++;
                            currentLastEtag = doc.Etag;

                            if (currentDocsCount % batchSize == 0)
                                ReportProgress("documents", currentDocsCount, totalDocsCount);
                        }
                    });
                }
                catch (Exception e)
                {
                    currentDocsCount++;
                    ReportCorruptedDocumentWithEtag("documents", currentDocsCount, e.Message, currentLastEtag);
                }
                finally
                {
                    if (currentDocsCount > previousDocsCount)
                        ReportProgress("documents", currentDocsCount, totalDocsCount);
                }
            } while (currentDocsCount > totalDocsCount);
        }

        private void WriteTransformers(JsonTextWriter jsonWriter)
        {
            var indexDefinitionsBasePath = Path.Combine(baseDirectory, indexDefinitionFolder);
            var transformers = Directory.GetFiles(indexDefinitionsBasePath, "*.transform");
            var currentTransformerCount = 0;
            foreach (var file in transformers)
            {
                var ravenObj = RavenJObject.Parse(File.ReadAllText(file));
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName("name");
                jsonWriter.WriteValue(ravenObj.Value<string>("Name"));
                jsonWriter.WritePropertyName("definition");
                ravenObj.WriteTo(jsonWriter);
                jsonWriter.WriteEndObject();
                currentTransformerCount++;
                ReportProgress("transformers", currentTransformerCount, transformers.Count());
            }
        }

        private void WriteIndexes(JsonTextWriter jsonWriter)
        {
            var indexDefinitionsBasePath = Path.Combine(baseDirectory, indexDefinitionFolder);
            var indexes = Directory.GetFiles(indexDefinitionsBasePath, "*.index");
            int currentIndexCount = 0;
            foreach (var file in indexes)
            {
                var ravenObj = RavenJObject.Parse(File.ReadAllText(file));
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName("name");
                jsonWriter.WriteValue(ravenObj.Value<string>("Name"));
                jsonWriter.WritePropertyName("definition");
                ravenObj.WriteTo(jsonWriter);
                jsonWriter.WriteEndObject();
                currentIndexCount++;
                ReportProgress("indexes", currentIndexCount, indexes.Count());
            }
        }

        private void WriteIdentities(JsonTextWriter jsonWriter)
        {
            long totalIdentities = 0;
            int currentIdentitiesCount = 0;
            do
            {
                storage.Batch(accsesor =>
                {
                    var identities = accsesor.General.GetIdentities(currentIdentitiesCount, batchSize, out totalIdentities);
                    var filteredIdentities = identities.Where(x=>FilterIdentity(x.Key));
                    foreach (var identityInfo in filteredIdentities)
                        {
                            new RavenJObject
                                {
                                    { "Key", identityInfo.Key }, 
                                    { "Value", identityInfo.Value }
                                }.WriteTo(jsonWriter);
                        }
                currentIdentitiesCount += identities.Count();
                ReportProgress("identities", currentIdentitiesCount, totalIdentities);
            });
            } while (totalIdentities > currentIdentitiesCount);
        }

        public bool FilterIdentity(string identityName)
        {
            if ("Raven/Etag".Equals(identityName, StringComparison.OrdinalIgnoreCase))
                return false;

            if ("IndexId".Equals(identityName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (Constants.RavenSubscriptionsPrefix.Equals(identityName, StringComparison.OrdinalIgnoreCase))
                return false;

            return false;
        }

        private void CreateTransactionalStorage(InMemoryRavenConfiguration ravenConfiguration)
        {
            if (string.IsNullOrEmpty(ravenConfiguration.DataDirectory) == false && Directory.Exists(ravenConfiguration.DataDirectory))
            {
                try
                {
                    if (TryToCreateTransactionalStorage(ravenConfiguration, HasCompression, Encryption, out storage) == false)
                        ConsoleUtils.PrintErrorAndFail("Failed to create transactional storage");
                }
                catch (UnauthorizedAccessException uae)
                {
                    ConsoleUtils.PrintErrorAndFail(string.Format("Failed to initialize the storage it is probably been locked by RavenDB.\nError message:\n{0}", uae.Message), uae.StackTrace);
                }
                catch (InvalidOperationException ioe)
                {
                    ConsoleUtils.PrintErrorAndFail(string.Format("Failed to initialize the storage it is probably been locked by RavenDB.\nError message:\n{0}", ioe.Message), ioe.StackTrace);
                }
                catch (Exception e)
                {
                    ConsoleUtils.PrintErrorAndFail(e.Message, e.StackTrace);
                    return;
                }

                return;
            }

            ConsoleUtils.PrintErrorAndFail(string.Format("Could not detect storage file under the given directory:{0}", ravenConfiguration.DataDirectory));
        }

        public static bool TryToCreateTransactionalStorage(InMemoryRavenConfiguration ravenConfiguration,
            bool hasCompression, EncryptionConfiguration encryption, out ITransactionalStorage storage)
        {
            storage = null;
            if (File.Exists(Path.Combine(ravenConfiguration.DataDirectory, Voron.Impl.Constants.DatabaseFilename)))
                storage = ravenConfiguration.CreateTransactionalStorage(InMemoryRavenConfiguration.VoronTypeName, () => { }, () => { });
            else if (File.Exists(Path.Combine(ravenConfiguration.DataDirectory, "Data")))
                storage = ravenConfiguration.CreateTransactionalStorage(InMemoryRavenConfiguration.EsentTypeName, () => { }, () => { });

            if (storage == null)
                return false;

            var orderedPartCollection = new OrderedPartCollection<AbstractDocumentCodec>();
            if (encryption != null)
            {
                var documentEncryption = new DocumentEncryption();
                documentEncryption.SetSettings(new EncryptionSettings(encryption.EncryptionKey, encryption.SymmetricAlgorithmType,
                    encryption.EncryptIndexes, encryption.PreferedEncryptionKeyBitsSize));
                orderedPartCollection.Add(documentEncryption);
            }
            if (hasCompression)
            {
                orderedPartCollection.Add(new DocumentCompression());
            }
                
            storage.Initialize(new SequentialUuidGenerator {EtagBase = 0}, orderedPartCollection);
            return true;
        }

        public static bool ValidateStorageExists(string dataDir)
        {
            return File.Exists(Path.Combine(dataDir, Voron.Impl.Constants.DatabaseFilename))
                   || File.Exists(Path.Combine(dataDir, "Data"));
        }

        private static readonly string indexDefinitionFolder = "IndexDefinitions";
        private readonly string baseDirectory;
        private readonly string outputDirectory;
        private ITransactionalStorage storage;
        private readonly int batchSize;
    }
}
