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

namespace FunctionAppImageProcess
{
    public static class FunctionBlobImageProcess
    {
        [FunctionName("BlobTriggerImageProcess")]
        public static async Task Run([BlobTrigger("testinput/{name}", Connection = "AzureWebJobsStorage")]Stream myBlob, [Blob("testoutput/{name}", FileAccess.ReadWrite, Connection = "AzureWebJobsStorage")]CloudBlockBlob outputBlob, string name, TraceWriter log)
        {
            // Start the HandleFile method.
            Task<(string containsTransformer, string containsPole)> customVisionTask = PassCustomVisionAsync(myBlob);
            Task<(string tags, string dominantColours, string accentColour, string isOnFire)> cognitiveVisionTask = PassCognitiveAsync(myBlob);
            Task<(string ocrTxt, string hasHighVoltageSign, string hasLiveElectricalSign, string hasLiveWiresSign)> ocrTask = PassOCRAsync(myBlob);

            (string exifCaptureDate, string exifCaptureTime, string exifLatGPS, string exifLongGPS) = SetGPSTags(myBlob);

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

            await outputBlob.UploadFromStreamAsync(myBlob);

            outputBlob.Properties.ContentType = MimeTypeMap.GetMimeType(extStr);

            if (ocrTask.Result.ocrTxt != null)
            {
                outputBlob.Metadata["ocrTxt"] = ocrTask.Result.ocrTxt;
                outputBlob.Metadata["hasHighVoltageSign"] = ocrTask.Result.hasHighVoltageSign;
                outputBlob.Metadata["hasLiveElectricalSign"] = ocrTask.Result.hasLiveElectricalSign;
                outputBlob.Metadata["hasLiveWiresSign"] = ocrTask.Result.hasLiveWiresSign;
            }
            if (cognitiveVisionTask.Result.tags != null)
            {
                outputBlob.Metadata["tags"] = cognitiveVisionTask.Result.tags;
                outputBlob.Metadata["dominantColours"] = cognitiveVisionTask.Result.dominantColours;
                outputBlob.Metadata["accentColour"] = cognitiveVisionTask.Result.accentColour;
                outputBlob.Metadata["isOnFire"] = cognitiveVisionTask.Result.isOnFire;
            }

            outputBlob.Metadata["containsTransformer"] = customVisionTask.Result.containsTransformer;
            outputBlob.Metadata["containsPole"] = customVisionTask.Result.containsPole;

            outputBlob.Metadata["exifCaptureDate"] = exifCaptureDate;
            outputBlob.Metadata["exifCaptureTime"] = exifCaptureTime;
            outputBlob.Metadata["exifLatGPS"] = exifLatGPS;
            outputBlob.Metadata["exifLongGPS"] = exifLongGPS;

            outputBlob.SetProperties();
            outputBlob.SetMetadata();
        }

        private static async Task<(string, string, string, string)> PassOCRAsync(Stream image)
        {
            string ocrTxt = ".";
            bool hasHighVoltageSign = false;
            bool hasLiveElectricalSign = false;
            bool hasLiveWiresSign = false;
            using (MemoryStream ms = new MemoryStream())
            {
                // create new memory stream so we don't have threading issues passing around the same stream
                image.Position = 0;
                image.CopyTo(ms);
                ms.Position = 0;
                image.Position = 0;

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
            return (ocrTxt, hasHighVoltageSign.ToString(), hasLiveElectricalSign.ToString(), hasLiveWiresSign.ToString());
        }

        private static async Task<(string, string, string, string)> PassCognitiveAsync(Stream image)
        {
            string tags = ".";
            string dominantColours = ".";
            string accentColour = ".";
            bool isOnFire = false;
            using (MemoryStream ms = new MemoryStream())
            {
                // create new memory stream so we don't have threading issues passing around the same stream
                image.Position = 0;
                image.CopyTo(ms);
                ms.Position = 0;
                image.Position = 0;

                try
                {
                    var visualFeatures = new VisualFeature[] { VisualFeature.Description, VisualFeature.Color };
                    VisionServiceClient visionService = new VisionServiceClient(Environment.GetEnvironmentVariable("visionSubscriptionKey"));
                    AnalysisResult cogTags = await visionService.AnalyzeImageAsync(ms, visualFeatures);

                    tags = string.Join(",", cogTags.Description.Tags);
                    dominantColours = string.Join(",", cogTags.Color.DominantColors);
                    accentColour = "#" + cogTags.Color.AccentColor;

                    foreach (string tag in cogTags.Description.Tags)
                    {
                        if ((tag == "fire") || (tag == "flame"))
                        {
                            // fire or flames are detected in the image - flag the image and trigger SMS workflow
                            isOnFire = true;
                        }
                    }

                }
                catch (Exception e)
                {
                }
            }
            return (tags, dominantColours, accentColour, isOnFire.ToString());
        }

        private static async Task<(string, string)> PassCustomVisionAsync(Stream image)
        {
            bool containsTransformer = false;
            bool containsPole = false;

            try
            {
                Guid projectId;
                using (MemoryStream ms = new MemoryStream())
                {
                    // create new memory stream so we don't have threading issues passing around the same stream

                    image.Position = 0;
                    image.CopyTo(ms);
                    ms.Position = 0;
                    image.Position = 0;

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
            return (containsTransformer.ToString(), containsPole.ToString());
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