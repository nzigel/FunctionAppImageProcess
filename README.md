# FunctionAppImageProcess
This Azure Function reads in a blob triggered when a new image is uploaded and performs custom image detection on it to look for trained images and then performs OCR to read text. I also uses Microsoft Cognitive Services Custom Vision to set tags on the image and sets the GPS data from the image if available.

There are two functions in this project one that reads from a container triggered by new blob images, it processes the image and writes metadata as attributes to the blob and outputs to an output conatiner.

The other function is trigger by a queue from https://github.com/nzigel/ImageUploadAPI that processes the image from the blob container and writes the metadata to a document DB document that is passed in from the queue.

## Machine Pre-requisites ##

Install the following on your machine:

- [Visual Studio 2017 Update 3 Preview](https://www.visualstudio.com/vs/preview/) with the following selected:
    - ASP.NET and Web development
    - Azure development
    - .NET Core cross-platform development

- [Latest Azure Functions tools for Visual Studio 2017](https://marketplace.visualstudio.com/vsgallery/e3705d94-7cc3-4b79-ba7b-f43f30774d28). When you first use this it will also install the Azure Functions CLI tools. 

- [Microsoft .NET Framework 4.6.2 Developer Pack](http://getdotnet.azurewebsites.net/target-dotnet-platforms.html)

For another sample with a similar pattern check out https://github.com/Azure-Samples/functions-customer-reviews 
