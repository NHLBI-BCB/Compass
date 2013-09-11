﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CSMSL;
using CSMSL.Analysis.Quantitation;
using CSMSL.IO;
using CSMSL.IO.Thermo;
using CSMSL.Proteomics;
using CSMSL.Spectral;
using LumenWorks.Framework.IO.Csv;

namespace Coon.Compass.TagQuant
{
    public class TagQuant
    {
        public bool DontQuantifyETD;
        public bool NoisebandCap;
        public bool MS3Quant;
        public SortedList<double, TagInformation> UsedTags;
        public MassTolerance ItMassTolerance;
        public MassTolerance FtMassTolerance;
        public string OutputDirectory;
        public string RawFileDirectory;
        public int ETDQuantPosition;
        public List<string> InputFiles;
        public Dictionary<string, ThermoRawFile> RawFiles;
        private StreamWriter logWriter;
        public Dictionary<TagSetType, IsobaricTagPurityCorrection> PurityMatrices;

        public event EventHandler<ProgressChangedEventArgs> ProgressChanged;
        public event EventHandler OnFinished; 

        private const double Carbon1213Difference = Constants.Carbon13 - Constants.Carbon;

        public TagQuant(string outputDirectory, string rawFileDirectory, IEnumerable<string> inputFiles,
            IEnumerable<TagInformation> tags, MassTolerance itTolerance, MassTolerance ftTolerance, bool isMS3Quant = false,
            int etdQuantPosition = 0, bool nosiebasecap = false, bool isETDQuantified = true)
        {
            RawFileDirectory = rawFileDirectory;
            OutputDirectory = outputDirectory;
            UsedTags = new SortedList<double, TagInformation>(tags.ToDictionary(t => t.MassCAD));
            InputFiles = inputFiles.ToList();
            NoisebandCap = nosiebasecap;
            DontQuantifyETD = isETDQuantified;
            MS3Quant = isMS3Quant;

            ItMassTolerance = itTolerance;
            FtMassTolerance = ftTolerance;
            ETDQuantPosition = etdQuantPosition;

            logWriter = new StreamWriter(Path.Combine(outputDirectory, "tagquant_log.txt"));
            logWriter.AutoFlush = true;

            RawFiles =
                Directory.EnumerateFiles(rawFileDirectory, "*.raw")
                    .ToDictionary(file => Path.GetFileNameWithoutExtension(file), file => new ThermoRawFile(file));
        }

        public void Run()
        {
            OnProgressUpdate(0);
            WriteLog();
            OnProgressUpdate(10);

            BuildPurityMatrixes(UsedTags.Values);
            OnProgressUpdate(25);

            List<QuantFile> quantFiles = LoadFiles(InputFiles, MS3Quant).ToList();
            OnProgressUpdate(50);

            Normalize(quantFiles);
            OnProgressUpdate(75);

            WriteOutputFiles(quantFiles);
            OnProgressUpdate(100);

            Log("\nEnd Time:\t" + DateTime.Now);
            logWriter.Close();
            var evt = OnFinished;
            if (evt != null)
            {
                evt(this, EventArgs.Empty);
            }
        }

        private void WriteLog()
        {
            Log(string.Format("===Tag Quant v{0}===",Assembly.GetExecutingAssembly().GetName().Version));
            Log("Start Time:\t" + DateTime.Now);
            Log("Use Noise Band Capping:\t" + NoisebandCap);
            Log("Use MS3 Quant:\t" + MS3Quant);
            Log("FT Mass Tolerance:\t" + FtMassTolerance);
            Log("IT Mass Tolerance:\t" + ItMassTolerance);
            Log("Input Raw Folder:\t" + RawFileDirectory);
            Log("Output Folder:\t" + OutputDirectory);
        }

        private void BuildPurityMatrixes(IEnumerable<TagInformation> inputTags)
        {
            List<TagInformation> tags = inputTags.ToList();

            Log("\n== Purity Correction Matrices ==");
            PurityMatrices = new Dictionary<TagSetType, IsobaricTagPurityCorrection>();
            foreach (TagSetType tagSet in Enum.GetValues(typeof(TagSetType)))
            {
                List<TagInformation> usedTags = tags.Where(t => t.TagSet == tagSet).ToList();
                if (usedTags.Count <= 0) 
                    continue;
                usedTags = usedTags.OrderBy(t => t.NominalMass).ToList();
                int max = usedTags[usedTags.Count - 1].NominalMass - usedTags[0].NominalMass + 1;
                int minint = usedTags[0].NominalMass;

                TagInformation[] tarray = new TagInformation[max];
                double[,] data = new double[max, 4];

                foreach (var tag in usedTags)
                {
                    tarray[tag.NominalMass - minint] = tag;
                }

                for (int i = 0; i < max; i++)
                {
                    var tag = tarray[i];
                    if (tag != null)
                    {
                        data[i, 0] = tag.M2;
                        data[i, 1] = tag.M1;
                        data[i, 2] = tag.P1;
                        data[i, 3] = tag.P2;
                    }
                    else
                    {
                        for (int j = 0; j < 4; j++) data[i, j] = 0;
                    }
                }
                IsobaricTagPurityCorrection correction = IsobaricTagPurityCorrection.Create(data);
                Log("Tag SetSite:\t"+tagSet);
                Log("Input Matrix");
                for (int i = 0; i < max; i++)
                {
                    Log(string.Format("\t{0:F1}\t{1:F1}\t{2:F1}\t{3:F1}", data[i, 0], data[i, 1], data[i, 2], data[i, 3]));
                }
                Log("\nPurity Matrix (determinant = "+correction.Determinant().ToString("g4")+")");
                double[,] purityMatrix = correction.GetMatrix();
                for (int i = 0; i < purityMatrix.GetLength(0); i++)
                {
                    StringBuilder sb = new StringBuilder("\t");
                    for (int j = 0; j < purityMatrix.GetLength(1); j++)
                    {
                        sb.AppendFormat("{0:f3}",purityMatrix[i, j]);
                        sb.Append('\t');
                    }
                    sb.Remove(sb.Length - 1, 1);
                    Log(sb.ToString());
                }
                Log("");
                PurityMatrices.Add(tagSet, correction);
            }
        }

        private void Normalize(IEnumerable<QuantFile> quantFiles)
        {
            double maxSignal =
                (from TagInformation tag in UsedTags.Values where tag.IsUsed select tag.TotalSignal).Concat(
                    new double[] {0}).Max();

            Log("\n== Normalization Data ==");

            Log(string.Format("\t{0,-8}\t{1,-8}\t{2,-8}\t{3,-13}\t{4,-8}\t{5,-4}\t{6,-4}\t{7,-4}\t{8,-4}", "Sample", "Tag", "m/z", "Total Signal", "Normalized", "-2", "-1", "+1", "+2"));
          

            foreach (TagInformation tag in UsedTags.Values)
            {
                // Divide by max so that everything is less than or equal to 1
                tag.NormalizedTotalSignal = tag.TotalSignal/maxSignal;

                Log(string.Format("\t{0,-8}\t{1,-8}\t{2,-8:f5}\t{3,-13:g3}\t{4,-8:g3}\t{5,-4:f2}\t{6,-4:f2}\t{7,-4:f2}\t{8,-4:f2}",
                    tag.SampleName, tag.TagName, tag.MassCAD, tag.TotalSignal,tag.NormalizedTotalSignal, tag.M2, tag.M1, tag.P1,
                    tag.P2));
            }

            foreach (QuantFile quantFile in quantFiles)
            {
                foreach (PSM psm in quantFile.Psms.Values)
                {
                    foreach (QuantPeak qpeak in psm.QuantPeaks.Values)
                    {
                        // Divide by the tags Normalized value again to normalize all channels to 1
                        qpeak.PurityCorrectedNormalizedIntensity = qpeak.PurityCorrectedIntensity/
                                                                   qpeak.Tag.NormalizedTotalSignal;
                    }
                }
            }
        }

        private void Log(string msg)
        {
            logWriter.WriteLine(msg);
        }

        private void WriteOutputFiles(IEnumerable<QuantFile> quantFiles)
        {
            foreach (QuantFile file in quantFiles)
            {
                string filePath = file.FilePath;
                string outputFilePath = filePath.Replace(".csv", "_quant.csv");
                using (CsvReader reader = new CsvReader(new StreamReader(filePath), true))
                {
                    using (StreamWriter writer = new StreamWriter(outputFilePath))
                    {
                        StringBuilder sb = new StringBuilder();
                        string[] headerColumns = reader.GetFieldHeaders();
                        int headerCount = headerColumns.Length;
                        string header = string.Join(",", headerColumns);
                        sb.Append(header);

                        // Raw Intensities
                        foreach (TagInformation tag in UsedTags.Values)
                        {
                            sb.AppendFormat(",{0} ({1} NL)", tag.SampleName, tag.TagName);
                        }

                        // Denormalized Intensities
                        foreach (TagInformation tag in UsedTags.Values)
                        {
                            sb.AppendFormat(",{0} ({1} dNL)", tag.SampleName, tag.TagName);
                        }

                        // Purity Corrected Intensities
                        foreach (TagInformation tag in UsedTags.Values)
                        {
                            sb.AppendFormat(",{0} ({1} PC)", tag.SampleName, tag.TagName);
                        }

                        // Purity Corrected Normalized Intensities
                        foreach (TagInformation tag in UsedTags.Values)
                        {
                            sb.AppendFormat(",{0} ({1} PCN)", tag.SampleName, tag.TagName);
                        }


                        // Number of Channels Detected
                        sb.Append(",Channels Detected");

                        writer.WriteLine(sb.ToString());

                        while (reader.ReadNextRecord())
                        {
                            sb.Clear();

                            string[] inputData = new string[headerCount];
                            reader.CopyCurrentRecordTo(inputData);
                            foreach (string data in inputData)
                            {
                                if (data.Contains(','))
                                {
                                    sb.Append("\"");
                                    sb.Append(data);
                                    sb.Append("\"");
                                }
                                else
                                {
                                    sb.Append(data);
                                }
                                sb.Append(',');
                            }
                            sb.Remove(sb.Length - 1, 1);
                            //sb.Append(string.Join(",", inputData));

                            //int scanNumber = int.Parse(reader["Spectrum number"]);
                            string fileId = reader["Filename/id"];

                            PSM psm = file[fileId];

                            List<QuantPeak> peaks =
                                (from TagInformation tag in UsedTags.Values select psm[tag]).ToList();

                            // Raw Intensities
                            foreach (QuantPeak peak in peaks)
                            {
                                sb.Append(',');
                                sb.Append(peak.RawIntensity);
                            }


                            // Denormalized Intensities
                            foreach (QuantPeak peak in peaks)
                            {
                                sb.Append(',');
                                sb.Append(peak.DeNormalizedIntensity);
                            }


                            // Purity Corrected Intensities
                            foreach (QuantPeak peak in peaks)
                            {
                                sb.Append(',');
                                sb.Append(peak.PurityCorrectedIntensity);
                            }


                            // Purity Corrected Normalized Intensities
                            foreach (QuantPeak peak in peaks)
                            {
                                sb.Append(',');
                                sb.Append(peak.PurityCorrectedNormalizedIntensity);
                            }

                            // Number of Channels Detected (positive raw intensity)
                            int channelsDetected = peaks.Count(p => p.RawIntensity > 0);
                            sb.Append(',');
                            sb.Append(channelsDetected);

                            writer.WriteLine(sb.ToString());
                        }
                    }
                }
            }
        }

        private IEnumerable<QuantFile> LoadFiles(IEnumerable<string> filePaths, bool ms3Quant = false)
        {
            MSDataFile.CacheScans = false;
            foreach (string filePath in filePaths)
            {
                Log("Processing file:\t" + filePath);
                QuantFile quantFile = new QuantFile(filePath);
                StreamReader basestreamReader = new StreamReader(filePath);
                using (CsvReader reader = new CsvReader(basestreamReader, true))
                {
                    while (reader.ReadNextRecord()) // go through csv and raw file to extract the info we want
                    {
                        int scanNumber = int.Parse(reader["Spectrum number"]);
                        string filenameID = reader["Filename/id"];
                        string rawFileName = filenameID.Split('.')[0];
                        ThermoRawFile rawFile;
                        if (!RawFiles.TryGetValue(rawFileName, out rawFile))
                        {
                            throw new ArgumentException("Cannot find this raw file: " + rawFileName + ".raw");
                        }
                        if (!rawFile.IsOpen)
                        {
                            rawFile.Open();
                        }

                        //// SetSite default fragmentation to CAD / HCD
                        //FragmentationMethod ScanFragMethod = filenameID.Contains(".ETD.")
                        //    ? FragmentationMethod.ETD
                        //    : FragmentationMethod.CAD;

                        //if (ScanFragMethod == FragmentationMethod.ETD)
                        //{
                        //    ScanFragMethod = FragmentationMethod.CAD;
                        //    scanNumber += ETDQuantPosition;
                        //}
                        
                        // Get the scan object for the sequence ms2 scan
                        MsnDataScan quantitationMsnScan = rawFile[scanNumber] as MsnDataScan;

                        if (quantitationMsnScan == null)
                        {
                            throw new ArgumentException("Spectrum Number " + scanNumber + " is not a valid MS2 scan");
                        }

                        if (MS3Quant)
                        {
                            quantitationMsnScan = null;
                            // Look forward to find associated MS3 quant scan (based on parent scan number)
                            int ms3ScanNumber = scanNumber + 1;
                            while (ms3ScanNumber < rawFile.LastSpectrumNumber)
                            {
                                if (rawFile.GetParentSpectrumNumber(ms3ScanNumber) == scanNumber)
                                {
                                    quantitationMsnScan = rawFile[ms3ScanNumber] as MsnDataScan;
                                    break;
                                }
                                ms3ScanNumber++;
                            }
                            if (quantitationMsnScan == null)
                            {
                                throw new ArgumentException("Cannot find a MS3 spectrum associated with spectrum number " + scanNumber);
                            }
                        }

                        MassTolerance massTolerance = quantitationMsnScan.MzAnalyzer == MZAnalyzerType.IonTrap2D ? ItMassTolerance : FtMassTolerance;
                        bool isETD = quantitationMsnScan.DissociationType == DissociationType.ETD;

                        double injectionTime = quantitationMsnScan.InjectionTime;
                        var massSpectrum = quantitationMsnScan.MassSpectrum;
                        double noise = 0;
                        if (NoisebandCap)
                        {
                            // Noise is pretty constant over a small region, find the noise of the center of all isobaric tags
                            MassRange range = new MassRange(UsedTags.Keys[0], UsedTags.Keys[UsedTags.Count - 1]);
                            ThermoLabeledPeak peak = massSpectrum.GetClosestPeak(range) as ThermoLabeledPeak;
                            if (peak != null)
                            {
                                noise = peak.Noise;
                            }
                            else
                            {
                                peak = massSpectrum.FirstPeak as ThermoLabeledPeak;
                                if (peak == null)
                                {
                                    throw new ArgumentException("Either the spectrum (#" + quantitationMsnScan.SpectrumNumber+") has no m/z peaks, or they are low-resolution data without noise information");
                                }
                                noise = peak.Noise;
                            }
                        }

                        Dictionary<TagInformation, QuantPeak> peaks = new Dictionary<TagInformation, QuantPeak>();

                        // Read in the peak data
                        foreach (TagInformation tag in UsedTags.Values)
                        {
                            double tagMz = isETD
                                ? tag.MassEtd
                                : tag.MassCAD;

                            var peak = massSpectrum.GetClosestPeak(tagMz, massTolerance);

                            QuantPeak qPeak = new QuantPeak(tag, peak, injectionTime, quantitationMsnScan, noise,
                                peak == null && NoisebandCap);

                            peaks.Add(tag, qPeak);
                        }

                        PurityCorrect(peaks.Values);

                        PSM psm = new PSM(filenameID, scanNumber, peaks);
                        quantFile.AddPSM(psm);
                    }
                }
                Log("PSMs Loaded:\t" + quantFile.Psms.Count );
                yield return quantFile;
            }

            // Dispose of all raw files
            foreach (ThermoRawFile rawFile in RawFiles.Values)
            {
                rawFile.Dispose();
            }
        }

        private void PurityCorrect(IEnumerable<QuantPeak> peaks)
        {
            List<QuantPeak> quantPeaks = peaks.ToList();

            foreach (TagSetType tagSet in PurityMatrices.Keys)
            {
                QuantPeak[] tagSetPeaks =
                    quantPeaks.Where(t => t.Tag.TagSet == tagSet).OrderBy(t => t.Tag.NominalMass).ToArray();

                int minint = tagSetPeaks[0].Tag.NominalMass;
                int max = tagSetPeaks[tagSetPeaks.Length - 1].Tag.NominalMass - minint + 1;

                QuantPeak[] finalArray = new QuantPeak[max];
                foreach (var qpeak in tagSetPeaks)
                {
                    finalArray[qpeak.Tag.NominalMass - minint] = qpeak;
                }

                double[] dNLvalues = new double[max];
                for (int i = 0; i < max; i++)
                {
                    var qpeak = finalArray[i];
                    if (qpeak == null || qpeak.IsNoisedCapped)
                    {
                        dNLvalues[i] = 0;
                    }
                    else
                    {
                        dNLvalues[i] = qpeak.DeNormalizedIntensity;
                    }
                }

                double[] correctedData = PurityMatrices[tagSet].ApplyPurityCorrection(dNLvalues);

                for (int i = 0; i < correctedData.Length; i++)
                {
                    var qpeak = finalArray[i];
                    if (qpeak == null || qpeak.IsNoisedCapped)
                        continue;

                    qpeak.Tag.TotalSignal += qpeak.PurityCorrectedIntensity = correctedData[i];
                }
            }
        }

        private void OnProgressUpdate(int percent)
        {
            var delgate = ProgressChanged;
            if (delgate != null)
            {
                delgate(this, new ProgressChangedEventArgs(percent, null));
            }
        }
    }
}
