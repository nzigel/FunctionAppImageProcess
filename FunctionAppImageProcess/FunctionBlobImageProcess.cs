﻿using System.IO;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Blob;
using MimeTypes;
using ExifLib;
using System;
using Microsoft.Cognitive.CustomVision;
using Microsoft.ProjectOxford.Vision;
using Microsoft.ProjectOxford.Vision.Contract;
using System.Text;
using System.Linq;
using Newtonsoft.Json;

namespace FunctionAppImageProcess
{
    public static class FunctionBlobImageProcess
    {
        // Function that takes an blob image from an input container and writes metadata back as an attribute and saves the image into an output container.
        [FunctionName("BlobTriggerImageProcess")]
        public static async Task Run([BlobTrigger("inputcontainer/{name}", Connection = "AzureWebJobsStorage")]Stream image, [Blob("outputcontainer/{name}", FileAccess.ReadWrite, Connection = "AzureWebJobsStorage")]CloudBlockBlob outputBlob, string name, TraceWriter log)
        {
            // Start the HandleFile method.
            Task<(bool containsTransformer, bool containsPole)> customVisionTask = PassCustomVisionAsync(image);
            Task<(string tags, string dominantColours, string accentColour, bool isOnFire)> cognitiveVisionTask = PassCognitiveAsync(image);
            Task<(string ocrTxt, bool hasHighVoltageSign, bool hasLiveElectricalSign, bool hasLiveWiresSign)> ocrTask = PassOCRAsync(image);

            (string exifCaptureDate, string exifCaptureTime, string exifLatGPS, string exifLongGPS) = SetGPSTags(image);

            string extStr = "";
            try
            {
                extStr = name.Split('.')[name.Split('.').Length - 1].ToLower();
            }
            catch (Exception e)
            {
                log.Info($"Mime Exception: {e.ToString()}");
            }

            customVisionTask.Wait(); // ensure that the custom vision process is complete
            cognitiveVisionTask.Wait(); //ensure that the cognitive services process is complete
            ocrTask.Wait(); // ensure the OCR task is complete

            await outputBlob.UploadFromStreamAsync(image);

            outputBlob.Properties.ContentType = MimeTypeMap.GetMimeType(extStr);

            ImageMetadata imgData = new ImageMetadata
            {
                MediaUrl = name,
                OcrTxt = ocrTask.Result.ocrTxt,
                HasHighVoltageSign = ocrTask.Result.hasHighVoltageSign,
                HasLiveElectricalSign = ocrTask.Result.hasLiveElectricalSign,
                HasLiveWiresSign = ocrTask.Result.hasLiveWiresSign,
                Tags = cognitiveVisionTask.Result.tags,
                DominantColours = cognitiveVisionTask.Result.dominantColours,
                AccentColour = cognitiveVisionTask.Result.accentColour,
                IsOnFire = cognitiveVisionTask.Result.isOnFire,
                ContainsTransformer = customVisionTask.Result.containsTransformer,
                ContainsPole = customVisionTask.Result.containsPole,
                ExifCaptureDate = exifCaptureDate,
                ExifCaptureTime = exifCaptureTime,
                ExifLatGPS = exifLatGPS,
                ExifLongGPS = exifLongGPS
            };


            outputBlob.Metadata["ocrTxt"] = imgData.OcrTxt;
            outputBlob.Metadata["hasHighVoltageSign"] = imgData.HasHighVoltageSign.ToString();
            outputBlob.Metadata["hasLiveElectricalSign"] = imgData.HasLiveElectricalSign.ToString();
            outputBlob.Metadata["hasLiveWiresSign"] = imgData.HasLiveWiresSign.ToString();
            outputBlob.Metadata["tags"] = imgData.Tags;
            outputBlob.Metadata["dominantColours"] = imgData.DominantColours;
            outputBlob.Metadata["accentColour"] = imgData.AccentColour;
            outputBlob.Metadata["isOnFire"] = imgData.IsOnFire.ToString();
            outputBlob.Metadata["containsTransformer"] = imgData.ContainsTransformer.ToString();
            outputBlob.Metadata["containsPole"] = imgData.ContainsPole.ToString();
            outputBlob.Metadata["exifCaptureDate"] = imgData.ExifCaptureDate;
            outputBlob.Metadata["exifCaptureTime"] = imgData.ExifCaptureTime;
            outputBlob.Metadata["exifLatGPS"] = imgData.ExifLatGPS;
            outputBlob.Metadata["exifLongGPS"] = imgData.ExifLongGPS;

            outputBlob.SetProperties();
            outputBlob.SetMetadata();

        }

        private class ImageMetadata
        {
            [JsonProperty(PropertyName = "id")]
            public Guid Id { get; set; }

            public string MediaUrl { get; set; }

            public string OcrTxt { get; set; }
            public bool? HasHighVoltageSign { get; set; }
            public bool? HasLiveElectricalSign { get; set; }
            public bool? HasLiveWiresSign { get; set; }
            public string Tags { get; set; }
            public string DominantColours { get; set; }
            public string AccentColour { get; set; }
            public bool? IsOnFire { get; set; }
            public bool? ContainsTransformer { get; set; }
            public bool? ContainsPole { get; set; }
            public string ExifCaptureDate { get; set; }
            public string ExifCaptureTime { get; set; }
            public string ExifLatGPS { get; set; }
            public string ExifLongGPS { get; set; }

            public DateTime Created { get; set; }
        }

        private static async Task<(string, bool, bool, bool)> PassOCRAsync(Stream image)
        {
            object _locker = new object();

            string ocrTxt = ".";
            bool hasHighVoltageSign = false;
            bool hasLiveElectricalSign = false;
            bool hasLiveWiresSign = false;
            using (MemoryStream ms = new MemoryStream())
            {
                // create new memory stream so we don't have threading issues passing around the same stream
                lock (_locker)
                {
                    // perform the copy an reset process on the stream inside a lock to be thread safe
                    image.Position = 0;
                    image.CopyTo(ms);
                    ms.Position = 0;
                    image.Position = 0;
                }

                try
                {
                    VisionServiceClient visionService = new VisionServiceClient(Environment.GetEnvironmentVariable("visionSubscriptionKey"));
                    OcrResults ocrResult = await visionService.RecognizeTextAsync(ms, "en");

                    ocrTxt = ParseOcr(ocrResult);

                    if (ocrTxt == "")
                    {
                        // no text was detected
                        ocrTxt = "."; // set the default to avoid empty string assignment to metadata
                    }
                    else
                    {
                        hasHighVoltageSign = ((ocrTxt.ToLower().Contains("danger")) && (ocrTxt.ToLower().Contains("high")) && (ocrTxt.ToLower().Contains("voltage")));
                        hasLiveElectricalSign = ((ocrTxt.ToLower().Contains("danger")) && (ocrTxt.ToLower().Contains("live")) && ((ocrTxt.ToLower().Contains("electrical")) || (ocrTxt.ToLower().Contains("equipment"))));
                        hasLiveWiresSign = ((ocrTxt.ToLower().Contains("danger")) && (ocrTxt.ToLower().Contains("live")) && (ocrTxt.ToLower().Contains("wires")));
                    }
                }
                catch (Exception e)
                {
                }
            }
            return (ocrTxt, hasHighVoltageSign, hasLiveElectricalSign, hasLiveWiresSign);
        }

        private static async Task<(string, string, string, bool)> PassCognitiveAsync(Stream image)
        {
            object _locker = new object();

            string tags = ".";
            string dominantColours = ".";
            string accentColour = ".";
            bool isOnFire = false;
            using (MemoryStream ms = new MemoryStream())
            {
                // create new memory stream so we don't have threading issues passing around the same stream
                lock (_locker)
                {
                    // perform the copy an reset process on the stream inside a lock to be thread safe
                    image.Position = 0;
                    image.CopyTo(ms);
                    ms.Position = 0;
                    image.Position = 0;
                }

                try
                {
                    var visualFeatures = new VisualFeature[] { VisualFeature.Description, VisualFeature.Color };
                    VisionServiceClient visionService = new VisionServiceClient(Environment.GetEnvironmentVariable("visionSubscriptionKey"));
                    AnalysisResult cogTags = await visionService.AnalyzeImageAsync(ms, visualFeatures);

                    tags = string.Join(",", cogTags.Description.Tags);
                    dominantColours = string.Join(",", cogTags.Color.DominantColors);
                    accentColour = "#" + cogTags.Color.AccentColor;

                    // fire or flames are detected in the image - flag the image and trigger SMS workflow
                    isOnFire = cogTags.Description.Tags.Take(5).Contains("fire") || cogTags.Description.Tags.Take(5).Contains("flame");

                }
                catch (Exception e)
                {
                }
            }
            return (tags, dominantColours, accentColour, isOnFire);
        }

        private static async Task<(bool, bool)> PassCustomVisionAsync(Stream image)
        {
            object _locker = new object();

            bool containsTransformer = false;
            bool containsPole = false;

            try
            {
                Guid projectId;
                using (MemoryStream ms = new MemoryStream())
                {
                    // create new memory stream so we don't have threading issues passing around the same stream
                    lock (_locker)
                    {
                        // perform the copy an reset process on the stream inside a lock to be thread safe
                        image.Position = 0;
                        image.CopyTo(ms);
                        ms.Position = 0;
                        image.Position = 0;
                    }

                    try
                    {
                        // Create the Api, passing in a credentials object that contains the training key

                        // There is a trained endpoint with custom vision api, it can be used to make a prediction
                        // Create a prediction endpoint, passing in a prediction credentials object that contains the obtained prediction key
                        PredictionEndpointCredentials predictionEndpointCredentials = new PredictionEndpointCredentials(Environment.GetEnvironmentVariable("predictionKey"));
                        PredictionEndpoint endpoint = new PredictionEndpoint(predictionEndpointCredentials);


                        Guid.TryParse(Environment.GetEnvironmentVariable("projectId"), out projectId);
                        // Make a prediction against the trained model

                        Microsoft.Cognitive.CustomVision.Models.ImagePredictionResultModel customresult = await endpoint.PredictImageAsync(projectId, ms);

                        double threshold = 0.7;
                        // Loop over each prediction and write out the results
                        foreach (var c in customresult.Predictions)
                        {
                            if ((c.Tag == "Transformer") && (c.Probability > threshold))
                            {
                                containsTransformer = true;
                            }
                            else if ((c.Tag == "Power Pole") && (c.Probability > threshold))
                            {
                                containsPole = true;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                    }

                }

            }
            catch { }
            return (containsTransformer, containsPole);
        }

        private static (string, string, string, string) SetGPSTags(Stream image)
        {
            string exifCaptureDate = ".";
            string exifCaptureTime = ".";
            string exifLatGPS = ".";
            string exifLongGPS = ".";

            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    // create new memory stream so we don't have threading issues passing around the same stream

                    image.Position = 0;
                    image.CopyTo(ms);
                    ms.Position = 0;
                    image.Position = 0;

                    using (ExifReader reader = new ExifReader(ms))
                    {
                        // "E" or "W" ("W" will be a negative longitude)

                        if (reader.GetTagValue<DateTime>(ExifTags.DateTimeDigitized,
                                                        out DateTime datePictureTaken))
                        {
                            exifCaptureDate = datePictureTaken.ToString("MMddyyyy");
                            exifCaptureTime = datePictureTaken.ToString("HHmmss");
                        }

                        if (reader.GetTagValue(ExifTags.GPSLatitude, out double[] latitudeComponents)
                            && reader.GetTagValue(ExifTags.GPSLongitude, out double[] longitudeComponents)
                            && reader.GetTagValue(ExifTags.GPSLatitudeRef, out string latitudeRef)
                            && reader.GetTagValue(ExifTags.GPSLongitudeRef, out string longitudeRef))
                        {
                            exifLatGPS = ConvertDegreeAngleToDouble(latitudeComponents[0], latitudeComponents[1], latitudeComponents[2], latitudeRef).ToString();
                            exifLongGPS = ConvertDegreeAngleToDouble(longitudeComponents[0], longitudeComponents[1], longitudeComponents[2], longitudeRef).ToString();
                        }
                    }

                }
            }
            catch (Exception e)
            {
            }
            return (exifCaptureDate, exifCaptureTime, exifLatGPS, exifLongGPS);
        }

        private static double ConvertDegreeAngleToDouble(double degrees, double minutes, double seconds, string latLongRef)
        {
            double result = ConvertDegreeAngleToDouble(degrees, minutes, seconds);
            if (latLongRef == "S" || latLongRef == "W")
            {
                result *= -1;
            }
            return result;
        }

        private static double ConvertDegreeAngleToDouble(double degrees, double minutes, double seconds)
        {
            return degrees + (minutes / 60) + (seconds / 3600);
        }

        private static string ParseOcr(OcrResults results)
        {
            StringBuilder stringBuilder = new StringBuilder();

            if (results != null && results.Regions != null)
            {
                foreach (var item in results.Regions)
                {
                    foreach (var line in item.Lines)
                    {
                        foreach (var word in line.Words)
                        {
                            stringBuilder.Append(word.Text);
                            stringBuilder.Append(" ");
                        }

                        stringBuilder.Append(",");
                    }
                }
            }
            return stringBuilder.ToString();
        }
    }
}