using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.ProjectOxford.Vision;
using ExifLib;
using Microsoft.Cognitive.CustomVision;
using Microsoft.ProjectOxford.Vision.Contract;
using System.Text;

namespace FunctionAppImageProcess
{
    public static class FunctionBlobQueueImageProcess
    {
        // Function that takes an object from a queue with a link to a DocumentDB document and an Image Blob - writes metadata back to the document in document DB
        [FunctionName("ProcessImageMetadata")]
        public static async Task ReviewImageAndText(
            [QueueTrigger("%queue-name%", Connection = "AzureWebJobsStorage")]  RequestItem queueInput,
            [Blob("images/{BlobName}", FileAccess.Read, Connection = "AzureWebJobsStorage")]  Stream image,
            [DocumentDB("db", "imageCollection", Id = "{DocumentId}", ConnectionStringSetting = "docDBConnectionString")]  dynamic inputDocument)
        {
            // Start the HandleFile method.
            (bool containsTransformer, bool containsPole) customVisionTask = await PassCustomVisionAsync(image);
            (string tags, string dominantColours, string accentColour, bool isOnFire) cognitiveVisionTask = await PassCognitiveAsync(image);
            (string ocrTxt, bool hasHighVoltageSign, bool hasLiveElectricalSign, bool hasLiveWiresSign) ocrTask = await PassOCRAsync(image);
            (string exifCaptureDate, string exifCaptureTime, string exifLatGPS, string exifLongGPS) = SetGPSTags(image);

            inputDocument.OcrTxt = ocrTask.ocrTxt;
            inputDocument.HasHighVoltageSign = ocrTask.hasHighVoltageSign;
            inputDocument.HasLiveElectricalSign = ocrTask.hasLiveElectricalSign;
            inputDocument.HasLiveWiresSign = ocrTask.hasLiveWiresSign;
            inputDocument.Tags = cognitiveVisionTask.tags;
            inputDocument.DominantColours = cognitiveVisionTask.dominantColours;
            inputDocument.AccentColour = cognitiveVisionTask.accentColour;
            inputDocument.IsOnFire = cognitiveVisionTask.isOnFire;
            inputDocument.ContainsTransformer = customVisionTask.containsTransformer;
            inputDocument.ContainsPole = customVisionTask.containsPole;
            inputDocument.ExifCaptureDate = exifCaptureDate;
            inputDocument.ExifCaptureTime = exifCaptureTime;
            inputDocument.ExifLatGPS = exifLatGPS;
            inputDocument.ExifLongGPS = exifLongGPS;

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
    

        public class RequestItem
            {
                public string DocumentId { get; set; }
                public string BlobName { get; set; }
            }
        }
}
