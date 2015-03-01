using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.Common;
using System.Runtime.InteropServices.WindowsRuntime;        // required to get the writeable bitmap extension
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using KinectPhotobooth.Log;

namespace KinectPhotobooth
{
    class AzureStorageManager
    {
        private static StorageFolder targetFolder = KnownFolders.CameraRoll;

        public static async Task<Uri> SaveToStorage(WriteableBitmap image, string filename)
        {
            EventSource.Log.Debug("SaveToStorage()");

            // Our Azure blob credentials
            StorageCredentials cred = new StorageCredentials("photokinect", "<YOUR KEY HERE>");

            // Retrieve storage account using the connection credentials above
            CloudStorageAccount storageAccount = new CloudStorageAccount(cred, true);

            // Create the blob client
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

            // Retrieve a reference to a container
            CloudBlobContainer container = blobClient.GetContainerReference("photos");

            // Create the container if it doesn't already exist.
            await container.CreateIfNotExistsAsync();
            await container.SetPermissionsAsync( new BlobContainerPermissions { PublicAccess = BlobContainerPublicAccessType.Blob });

            // Get the pixel buffer of the writable bitmap in bytes
            byte[] pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(image.PixelBuffer);

            //Encoding the data of the PixelBuffer we have from the writable bitmap
            var inMemoryRandomStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, inMemoryRandomStream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();

            // Retrieve reference to a blob named with the input variable (filename)
            CloudBlockBlob blockBlob = container.GetBlockBlobReference(filename);
            blockBlob.Properties.ContentType = "image/png";

            // Create or overwrite the created blob with contents from our image
            await blockBlob.UploadFromStreamAsync(inMemoryRandomStream);

            // return the Uri
            return blockBlob.Uri;
        }

        public static async Task<WriteableBitmap> ConvertUriToQRCode(Uri uri)
        {
            EventSource.Log.Debug("ConvertUrlToQRCode()");

            // Set up our encoding options for the barcode
            EncodingOptions eo = new EncodingOptions();
            eo.Height = 1000;
            eo.Width = 1000;

            var writer = new BarcodeWriter { Format = BarcodeFormat.QR_CODE, Options = eo };
            var qrCodeBitmap = writer.Write(uri.ToString());

            WriteableBitmap wb = new WriteableBitmap(eo.Width, eo.Height);
            wb = (WriteableBitmap)qrCodeBitmap.ToBitmap();
            
            DateTime now = DateTime.Now;
            string fileName = "QRCode" + "-" + now.ToString("s").Replace(":", "-") + "-" + now.Millisecond.ToString() + ".jpg";

            StorageFile file = await targetFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);

            IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            Stream pixelStream = wb.PixelBuffer.AsStream();
            
            byte[] pixels = new byte[pixelStream.Length];
            await pixelStream.ReadAsync(pixels, 0, pixels.Length);

            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)wb.PixelWidth, (uint)wb.PixelHeight, 96.0, 96.0, pixels);

            // clean up
            pixelStream.Dispose();
            await encoder.FlushAsync();
            await stream.FlushAsync();
            stream.Dispose();

            return wb;
        }
    }
}
