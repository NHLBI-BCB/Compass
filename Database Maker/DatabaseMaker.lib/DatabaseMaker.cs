﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CSMSL.IO;

namespace Coon.Compass.DatabaseMaker
{
    public class DatabaseMaker
    {        
        public DatabaseMakerOptions Options { get; set; }

        public DatabaseMaker(DatabaseMakerOptions options)
        {
            Options = options;
        }

        public DatabaseMaker(IEnumerable<string> InputFiles, DatabaseType OutputType,
            DecoyDatabaseMethod DecoyType, bool ExcludeNTerminalResidue, bool ExcludeNTerminalMethionine,
            bool BlastDatabase, bool DoNotMergeFiles, string formatdbFilepath, string OutputFastaFile, string DecoyPrefix)
        {
            Options = new DatabaseMakerOptions();
            Options.InputFiles = new List<string>(InputFiles);
            Options.OutputType = OutputType;
            Options.DecoyType = DecoyType;
            Options.OutputFastaFile = OutputFastaFile;
            Options.ExcludeNTerminalMethionine = ExcludeNTerminalMethionine;
            Options.ExcludeNTerminalResidue = ExcludeNTerminalResidue;
            Options.BlastDatabase = BlastDatabase;
            Options.DoNotMergeFiles = DoNotMergeFiles;
            Options.DecoyPrefix = DecoyPrefix;
        }

        public void CreateDatabase()
        {
            try
            {
                string outputFolder = Path.GetDirectoryName(Options.OutputFastaFile);                
                if(!Directory.Exists(outputFolder))
                {
                    outputFolder = Directory.GetCurrentDirectory();
                }

                if (Options.GenerateLogFile)
                {
                    GenerateLog(outputFolder);
                }

                string ext = Options.FileExtension; // ".fasta"
                string output_filename = Path.GetFileNameWithoutExtension(Options.OutputFastaFile); // options.OutputFile

                if (Options.DoNotAppendDatabaseType == false)
                {
                    switch (Options.OutputType)
                    {
                        case DatabaseType.Target:
                            output_filename += "_TARGET" + ext;
                            break;
                        case DatabaseType.Decoy:
                            output_filename += "_DECOY" + ext;
                            break;
                        case DatabaseType.Concatenated:
                        default:
                            output_filename += "_CONCAT" + ext;
                            break;
                    }
                }

                else
                {
                    output_filename = output_filename + ext;
                }
                string outputPath = Path.Combine(outputFolder, output_filename);
                
                if (Options.DoNotMergeFiles == false)
                  {
                     using (FastaWriter writer = new FastaWriter(outputPath))
                       {
                          foreach (string fastaFile in Options.InputFiles)
                            {
                               WriteFasta(fastaFile, writer);
                            }
                        }
                   }
                else
                  {
                     foreach (string fastaFile in Options.InputFiles)
                       {
                          using (FastaWriter writer = new FastaWriter(outputPath))
                            {
                               WriteFasta(fastaFile, writer);
                            }
                        }
                   }
                
                if (Options.BlastDatabase)
                {
                    MakeBlastDatabase(outputFolder, output_filename, Path.GetFileNameWithoutExtension(output_filename));
                }
                // //Give feedback to user.
                //MessageBox.Show("Database saved to " + outputFolder);
            }          
            
            catch (DirectoryNotFoundException)
            {
                //MessageBox.Show("Output Folder Path does not exist.");
            }
        }
    

        /// <summary>
        /// Generates a log file detailing the parameters used
        /// </summary>
        /// <param name="logFileName">The name of the log file to write</param>
        private void GenerateLog(string outputDirectory, string logFileName = "DatabaseMaker.log")
        {
            using (StreamWriter log = new StreamWriter(Path.Combine(outputDirectory, logFileName)))
            {                
                log.WriteLine("Database Maker PARAMETERS");
                log.WriteLine("Database Type: {0}", Options.OutputType);
                if (Options.OutputType != DatabaseType.Target)
                {
                    log.WriteLine("Decoy Database Method: {0}", Options.DecoyType);
                    log.WriteLine("Exclude N-Terminus: {0}", Options.ExcludeNTerminalResidue);
                    if (Options.ExcludeNTerminalResidue)
                    {
                        log.WriteLine("Only If N-Terminus Is Methionine: {0}",Options.ExcludeNTerminalMethionine);
                    }
                }
                log.WriteLine();
                log.WriteLine(Options.DoNotMergeFiles ? "Fasta Files:" : "Merging Fasta Files:");
                foreach (string fastafile in Options.InputFiles)
                {
                    log.WriteLine(fastafile);
                }                
            }
        }

        public void WriteFasta(string fasta_file, FastaWriter Writer)
        {
            using (FastaReader reader = new FastaReader(fasta_file))
            {
                foreach (Fasta fasta in reader.ReadNextFasta())
                {
                    if (Options.OutputType == DatabaseType.Target || Options.OutputType == DatabaseType.Concatenated)
                    {
                        Writer.Write(fasta);
                    }
                    if (Options.OutputType == DatabaseType.Decoy || Options.OutputType == DatabaseType.Concatenated)
                    {
                        Writer.Write(fasta.ToDecoy(Options.DecoyPrefix, Options.DecoyType, Options.ExcludeNTerminalResidue, Options.ExcludeNTerminalMethionine));
                    }
                }
            }
        }


        public static string MAKEBLASTDB_FILENAME = "makeblastdb.exe";
        public static string LOG_FILENAME = "blast_log.txt";

        /**
         * Create BLAST Database files for OMMSA by use makeblastdb.exe\
         **/
        public static void MakeBlastDatabase(string outputfolder, string infile, string outfile)
        {
            Process process = new Process();
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WorkingDirectory = outputfolder;
            process.StartInfo.UseShellExecute = false;

            // Pick makeblastdb.exe from Application Directory Folder
            string filename = Path.Combine(Environment.CurrentDirectory, MAKEBLASTDB_FILENAME);
            process.StartInfo.FileName = filename;// Path.GetDirectoryName(System.Environment.CurrentDirectory) + @"\" + MAKEBLASTDB_FILENAME;

            process.StartInfo.Arguments = string.Format("-in {0} -out {1} -max_file_sz {2} -logfile {3}", infile, outfile, "2GB", LOG_FILENAME);
            process.Start();
            process.WaitForExit();
        }

    }
}