using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Xml.Dom;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Media.Imaging;
using WindowsPreview.Kinect;
using Windows.Storage;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Storage.Pickers;
using System.Diagnostics;
using WinRTXamlToolkit;
using WinRTXamlToolkit.Controls;
using WinRTXamlToolkit.Common;
using Windows.UI.ApplicationSettings;
using Toolkit.MotionDetector;
using KinectPhotobooth.Log;
using Windows.Networking.Sockets;
using Windows.Web;
using Windows.UI.Popups;

namespace KinectPhotobooth
{
    public sealed partial class MainPage : Page
    {
        #region Variables
        private const Int16 RDY_TIME = 1;
        private const Int16 COUNT_TIME = 5;                     // amount of time for countdown between pictures
        private const Int16 RESET_TIME = 30;                   // amount of time (in seconds) we show the QR code for a photo strip before we reset the app
        private const float MOTION_THRESHOLD = 0.005f;
        private const Int16 NUM_PHOTOS = 4;

        private const string DEFAULT_START_TEXT = "Press 'Start' to begin!";

        private const UInt16 IMAGE_WIDTH = 624;     //672
        private const UInt16 IMAGE_HEIGHT = 1416;   //1536

        private const UInt16 THUMB_WIDTH = 576;
        private const UInt16 THUMB_HEIGHT = 324;

        private const UInt16 BORDER = 24;   //48

        private DispatcherTimer readyTimer = null;

        private KinectSensor sensor = null;
        private ColorFrameReader colorReader;
        private MultiSourceFrameReader msfReader;
        private Body[] bodies;

        private MotionDetector md = null;

        private WriteableBitmap liveWB;
        private WriteableBitmap[] capturedImages = null;
        private byte[] colorPixels;
        private WriteableBitmap finalStitch;
        private string finalStitchName;
        private Uri finalStitchUri;
        private WriteableBitmap qrCodeImage;

        private List<ulong> trackedPeople = new List<ulong>();      // tracking IDs for any tracked bodies in frame
        private StorageFolder targetFolder = KnownFolders.CameraRoll;
        private bool takingPicture = false;
        //private ushort[] currentDepth;
        //private byte[] currentBodyImage;
        private List<DateTime> trackedMovedTime = new List<DateTime>(new DateTime[] { DateTime.Now, DateTime.Now, DateTime.Now, DateTime.Now, DateTime.Now, DateTime.Now });

        private Int16 currentPhotoNumber;

        private MessageWebSocket messageWebSocket;
        private DataWriter messageWriter;

        #endregion

        public MainPage()
        {
            // Log
            EventSource.Log.Debug("Initializing the MainPage");

            this.InitializeComponent();

            VisualStateManager.GoToState(this, "Ready", true);

            // Initialize sensor
            this.sensor = KinectSensor.GetDefault();

            // Open reader for color
            this.colorReader = this.sensor.ColorFrameSource.OpenReader();

            // Open reader for body, depth, bodyIndex
            this.msfReader = this.sensor.OpenMultiSourceFrameReader(FrameSourceTypes.Body | FrameSourceTypes.Depth | FrameSourceTypes.BodyIndex);

            // Initialize callback functions
            this.colorReader.FrameArrived += colorReader_FrameArrived;
            //this.msfReader.MultiSourceFrameArrived += msfReader_MultiSourceFrameArrived;
            FrameDescription colorDescription = sensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);

            // Initialize our buffer for body info
            this.bodies = new Body[this.sensor.BodyFrameSource.BodyCount];

            // Create our writable bitmaps
            this.liveWB = new WriteableBitmap(this.sensor.ColorFrameSource.FrameDescription.Width, this.sensor.ColorFrameSource.FrameDescription.Height);
            this.colorPixels = new byte[this.sensor.ColorFrameSource.FrameDescription.LengthInPixels * colorDescription.BytesPerPixel]; // each pixel is 4 bytes

            this.capturedImages = new WriteableBitmap[NUM_PHOTOS];
            for (int i = 0; i < NUM_PHOTOS; ++i)
            {
                this.capturedImages[i] = new WriteableBitmap(this.sensor.ColorFrameSource.FrameDescription.Width, this.sensor.ColorFrameSource.FrameDescription.Height);
            }

            // Initialize our arrays for the motion detector
            //this.currentDepth = new ushort[this.sensor.DepthFrameSource.FrameDescription.LengthInPixels];
            //this.currentBodyImage = new byte[this.sensor.BodyIndexFrameSource.FrameDescription.LengthInPixels];

            // Set our motion detector parameters
            //this.md = new MotionDetector();
            //md.EnableMotionImage(true);
            //md.SetMotionThreshold(MOTION_THRESHOLD);    // Cumulative amount of depth delta (in mm) that must be exceeded during 2 frames for a pixel to be considered "in motion" - default is 100 (10cm).  Increasing this value will require faster motions to trigger the detector.
            //md.SetNoiseFloorResetFrameCount();  // Number of low motion frames required to reset the noise floor to a new low - default is 10.  Increasing this value will increase the amount of "holding still" time required between motions.
            //md.SetMotionThreshold(MOTION_THRESHOLD);    // Amount of area (in meters squared) that needs to be in motion to trigger the detector - default is 0.001.  Increasing this value will require more pixels in motion (more of your body moving) to trigger the detector

            // Put our color image
            finalStitch = new WriteableBitmap(IMAGE_WIDTH, IMAGE_HEIGHT);
            this.imageLiveColor.Source = liveWB;

            //this.appBarButton_Picture.Click += appBarButton_Picture_Click;
            //this.appBarButton_Timer.Click += appBarButton_Timer_Click;
            this.appBarButton_Settings.Click += appBarButton_Settings_Click;
            this.timerCountdown.CountdownComplete += timerCountdown_CountdownComplete;
            this.timerCountdown.Tapped += timerCountdown_Tapped;

            this.finalCountdown.CountdownComplete += finalCountdown_CountdownComplete;

            // Open our sensor
            this.sensor.Open();

            // Start our app timer
            this.readyTimer = new DispatcherTimer();
            this.readyTimer.Interval = TimeSpan.FromSeconds(RDY_TIME);
            this.readyTimer.Tick += readyTimer_Tick;

            this.helpTips.Text = DEFAULT_START_TEXT;
        }

        


        void RestartPhotoSession()
        {
            EventSource.Log.Debug("RestartPhotoSession()");

            this.helpTips.Text = "Strike a pose...";

            // reset the photo count
            this.currentPhotoNumber = NUM_PHOTOS;

            // Kill our app timer & then restart it
            this.readyTimer.Start();
        }

        private async void PingCanon()
        {
            try
            {
                // Make a local copy to avoid races with Closed events.
                MessageWebSocket webSocket = messageWebSocket;

                // Have we connected yet?
                if (webSocket == null)
                {
                    Uri server = new Uri(@"ws://localhost:8080/service");

                    webSocket = new MessageWebSocket();
                    // MessageWebSocket supports both utf8 and binary messages.
                    // When utf8 is specified as the messageType, then the developer
                    // promises to only send utf8-encoded data.
                    webSocket.Control.MessageType = SocketMessageType.Utf8;

                    await webSocket.ConnectAsync(server);
                    messageWebSocket = webSocket; // Only store it after successfully connecting.
                    messageWriter = new DataWriter(webSocket.OutputStream);
                }

                string message = "cheese!";

                // Buffer any data we want to send.
                messageWriter.WriteString(message);

                // Send the data as one complete message.
                await messageWriter.StoreAsync();
            }
            catch (Exception ex) // For debugging
            {
                WebErrorStatus status = WebSocketError.GetStatus(ex.GetBaseException().HResult);
                // Add your specific error-handling code here.
            }
        }

        public async Task  TakePicture()
        {
            string currentFile = "";

            EventSource.Log.Debug("TakePicture()");

            if (!this.takingPicture)
            {
                this.takingPicture = true;

                // send websockets message to Canon
                Task ping = new Task(new Action(PingCanon));
                ping.Start();

                // play our shutter sound & save the image
                this.soundShutter.Play();

                WriteableBitmap wb = capturedImages[this.currentPhotoNumber - 1] = this.liveWB.Clone();
                wb.Invalidate();

                currentFile = this.targetFolder.Path + @"\";
                string curFileName = await SaveBitmapFile(wb); 
                currentFile += curFileName;
                System.Uri fileUri = new Uri(currentFile);

                
                try
                {
                    var _File = await KnownFolders.CameraRoll.GetFileAsync(curFileName);
                    var filestream = await _File.OpenAsync(FileAccessMode.Read);
                    BitmapImage bimp = new BitmapImage();
                    bimp.SetSource(filestream);
                    filestream.Dispose();
                   

                    switch (currentPhotoNumber)
                    {
                        case 4:
                            this.imageStrip01.Source = bimp;
                            break;
                        case 3:
                            this.imageStrip02.Source = bimp;
                            break;
                        case 2:
                            this.imageStrip03.Source = bimp;
                            break;
                        default:
                            this.imageStrip04.Source = bimp;
                            break;
                    }

                }
                catch (Exception)
                {
                    
                    throw;
                }

                this.currentPhotoNumber--;

                takingPicture = false;
            }

            if (this.currentPhotoNumber < 1)
            {
                VisualStateManager.GoToState(this, "Review", true);

                // stitch images
                await StitchImages(capturedImages);

                // Upload to Azure and get the Uri
                try
                {
                    this.helpTips.Text = "Creating the final photo strip...";
                    
                    var finalStitchUri = await AzureStorageManager.SaveToStorage(finalStitch, finalStitchName);

                    this.helpTips.Text = "Scan the code below to download your photo strip";
                    
                    VisualStateManager.GoToState(this, "QRCode", true);

                    // Generate QR Code & display it
                    // you need to wait on this to complete before proceeding
                    var qrCodeImage = await AzureStorageManager.ConvertUriToQRCode(finalStitchUri);
                    
                    this.capturedImageViewer.Source = qrCodeImage;

                    // reset the text and strip images -> then start the reset timer
                    await this.finalCountdown.StartCountdownAsync(RESET_TIME); 
                    this.helpTips.Text = DEFAULT_START_TEXT;
                    ResetThumbImages();
                    
                    
                }
                catch (Exception)
                {
                    throw;
                }

            }
            else
            {
                readyTimer.Start();
            }
        }

        async Task<WriteableBitmap> StitchImages(WriteableBitmap[] bitmaps)
        {
            double offset = 0;                      // counter for our loop below
            var resizedImages =                     // collection of our captured images
                (from image in this.capturedImages select image.Resize(THUMB_WIDTH, THUMB_HEIGHT, WriteableBitmapExtensions.Interpolation.Bilinear)).Reverse();

            // initialize the 'canvas' for the final stitched image -> set background to white
            finalStitch.Clear(Windows.UI.Colors.White);

            // log this method
            EventSource.Log.Debug("StitchImages()");

            foreach(WriteableBitmap image in resizedImages)
            {
                Point dest = new Point(BORDER,(THUMB_HEIGHT * offset) + BORDER + BORDER*offset);

                Rect sourceImageRect = new Rect(new Point(0, 0), new Point(THUMB_WIDTH, THUMB_HEIGHT));

                finalStitch.Blit(dest, image, sourceImageRect, Windows.UI.Colors.White, WriteableBitmapExtensions.BlendMode.None);

                offset++;
            }

            // set our file name
            finalStitchName = await SaveBitmapFile(finalStitch);

            return finalStitch;
        }


        #region UI Event Handlers

        async void readyTimer_Tick(object sender, object e)
        {
            EventSource.Log.Debug("readyTimer_Tick()");

            this.readyTimer.Stop();

            VisualStateManager.GoToState(this, "Countdown", true);

            await this.timerCountdown.StartCountdownAsync(COUNT_TIME);
        }

        async void timerCountdown_CountdownComplete(object sender, RoutedEventArgs e)
        {
            EventSource.Log.Debug("timerCountdown_CountdownComplete()");

            VisualStateManager.GoToState(this, "Flash", true);

            TakePicture();
        }

        void finalCountdown_CountdownComplete(object sender, RoutedEventArgs e)
        {
            EventSource.Log.Debug("finalCountdown_CountdownComplete()");

            //RestartPhotoSession();

            VisualStateManager.GoToState(this, "Ready", true);
        }

        async void buttonSave_Click(object sender, RoutedEventArgs e)
        {
            EventSource.Log.Debug("Clicked buttonSave");

            // Display confirmation of the save
            //await Log("Saved the picture");

            // Rename the photo to denote saved TODO

            RestartPhotoSession();
        }

        protected async void buttonRetake_Click(object sender, RoutedEventArgs e)
        {
            EventSource.Log.Debug("buttonRetake_Click()");
            RestartPhotoSession();
        }


        protected async void appBarButton_Settings_Click(object sender, RoutedEventArgs e)
        {
            EventSource.Log.Debug("appBarButton_Settings_Click()");
            Button b = sender as Button;
            if (b != null)
            {
                //SettingsFlyoutMain sfm = new SettingsFlyoutMain();

                // Note the use of ShowIndependent() here versus Show() in Scenario 2.
                //sfm.ShowIndependent();
            }
        }

        async void timerCountdown_Tapped(object sender, TappedRoutedEventArgs e)
        {
            EventSource.Log.Debug("timerCountdown_Tapped()");

            TakePicture();
        }

        async void appBarButton_Timer_Click(object sender, RoutedEventArgs e)
        {
            EventSource.Log.Debug("appBarButton_Timer_Click()");
            await this.timerCountdown.StartCountdownAsync(COUNT_TIME);
        }

        //private async void appBarButton_Picture_Click(object sender, RoutedEventArgs e)
        //{
        //    EventSource.Log.Debug("appBarButton_Picture_Click()");

        //    Task takePic = TakePicture();
        //    takePic.Start();
        //}

#endregion


        /// <summary>
        /// Invoked when this page is about to be displayed in a Frame.
        /// </summary>
        /// <param name="e">Event data that describes how this page was reached.  The Parameter
        /// property is typically used to configure the page.</param>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            EventSource.Log.Debug("OnNavigatedTo()");

            SettingsPane.GetForCurrentView().CommandsRequested += onCommandsRequested;
        }

        protected async override void OnNavigatedFrom(NavigationEventArgs e)
        {
            EventSource.Log.Debug("OnNavigatedFrom()");

            SettingsPane.GetForCurrentView().CommandsRequested -= onCommandsRequested;
        }

        /// <summary>
        /// Handler for the CommandsRequested event. Add custom SettingsCommands here.
        /// </summary>
        /// <param name="e">Event data that includes a vector of commands (ApplicationCommands)</param>
        protected void onCommandsRequested(SettingsPane settingsPane, SettingsPaneCommandsRequestedEventArgs e)
        {
            EventSource.Log.Debug("onCommandsRequested()");

            SettingsCommand generalCommand = new SettingsCommand("general", "General",
                (handler) =>
                {
                    //rootPage.NotifyUser("You selected the 'General' SettingsComand", NotifyType.StatusMessage);
                });

            e.Request.ApplicationCommands.Add(generalCommand);

            //SettingsCommand helpCommand = new SettingsCommand("help", "Help",
            //    (handler) =>
            //    {
            //        //rootPage.NotifyUser("You selected the 'Help' SettingsComand", NotifyType.StatusMessage);
            //    });
            //e.Request.ApplicationCommands.Add(helpCommand);
        }

        protected void colorReader_FrameArrived(ColorFrameReader sender, ColorFrameArrivedEventArgs args)
        {
            EventSource.Log.Debug("colorReader_FrameArrived()");

            using (ColorFrame colorFrame = args.FrameReference.AcquireFrame())
            {
                if (colorFrame != null)
                {
                    // get our color frame into our writeable bitmap
                    colorFrame.CopyConvertedFrameDataToArray(this.colorPixels, ColorImageFormat.Bgra);

                    Stream stream = this.liveWB.PixelBuffer.AsStream();
                    stream.Seek(0, SeekOrigin.Begin);
                    stream.Write(this.colorPixels, 0, colorPixels.Length);
                    this.liveWB.Invalidate();
                }
            }
        }

        protected async Task<string> SaveBitmapFile(WriteableBitmap wb)
        {
            EventSource.Log.Debug("SaveBitmapFile()");

            // Copy our current color image
            DateTime now = DateTime.Now;
            string fileName = "KinectPhotobooth" + "-" + now.ToString("s").Replace(":", "-") + "-" + now.Millisecond.ToString() + ".png";

            StorageFile file = await this.targetFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);

            IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            Stream pixelStream = wb.PixelBuffer.AsStream();
            byte[] pixels = new byte[pixelStream.Length];
            await pixelStream.ReadAsync(pixels, 0, pixels.Length);

            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)wb.PixelWidth, (uint)wb.PixelHeight, 96.0, 96.0, pixels);
            
            // clean up
            pixelStream.Dispose();
            await encoder.FlushAsync();
            await stream.FlushAsync();
            stream.Dispose();

            return fileName;
        }

        protected async void imageLiveColor_Tapped(object sender, TappedRoutedEventArgs e)
        {
            string currentFile;
            EventSource.Log.Debug("imageLiveColor_Tapped()");
            soundShutter.Play();
            currentFile = await SaveBitmapFile(liveWB);
        }

        protected async static Task<BitmapImage> BitmapFromByteAsync(byte[] inputBytes)
        {
            EventSource.Log.Debug("BitmapFromByteAsync()");
            if (inputBytes != null)
            {
                using (var s = new InMemoryRandomAccessStream())
                {
                    await s.WriteAsync(inputBytes.AsBuffer());

                    var image = new BitmapImage();
                    s.Seek(0);
                    image.SetSource(s);
                    return image;
                }
            }
            return null;
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            VisualStateManager.GoToState(this, "Ready", true);

            // Start a timer then reset
            // need more time here for people to take capture of QR code
            RestartPhotoSession();
        }

        private void ResetThumbImages()
        {
            BitmapImage img01 = new BitmapImage(new Uri("ms-appx:/Assets/thumb01.png", UriKind.RelativeOrAbsolute));
            BitmapImage img02 = new BitmapImage(new Uri("ms-appx:/Assets/thumb02.png", UriKind.RelativeOrAbsolute));
            BitmapImage img03 = new BitmapImage(new Uri("ms-appx:/Assets/thumb03.png", UriKind.RelativeOrAbsolute));
            BitmapImage img04 = new BitmapImage(new Uri("ms-appx:/Assets/thumb04.png", UriKind.RelativeOrAbsolute));

            this.imageStrip01.Source = img01;
            this.imageStrip02.Source = img02;
            this.imageStrip03.Source = img03;
            this.imageStrip04.Source = img04;
        }


        //void msfReader_MultiSourceFrameArrived(MultiSourceFrameReader sender, MultiSourceFrameArrivedEventArgs args)
        //{
        //    using (MultiSourceFrame msFrame = args.FrameReference.AcquireFrame())
        //    {
        //        if (msFrame != null)
        //        {
        //            // check for individual frames for Body, Depth, and BodyIndex
        //            using (BodyFrame bodyFrame = msFrame.BodyFrameReference.AcquireFrame())
        //            {
        //                using (DepthFrame depthFrame = msFrame.DepthFrameReference.AcquireFrame())
        //                {
        //                    using (BodyIndexFrame bodyIndexFrame = msFrame.BodyIndexFrameReference.AcquireFrame())
        //                    {
        //                        // ensure our frames are there
        //                        if (bodyFrame != null && depthFrame != null && bodyIndexFrame != null)
        //                        {
        //                            // Prep our data for Motion Detector
        //                            depthFrame.CopyFrameDataToArray(currentDepth);
        //                            md.Update(0, currentDepth, null);

        //                            Debug.WriteLine("area: " + md.GetMotionPixelCount().ToString());

        //                            //bodyIndexFrame.CopyFrameDataToArray(currentBodyImage);

        //                            //// Process body data
        //                            //bodyFrame.GetAndRefreshBodyData(this.bodies);
                                    
        //                            //int bodyIndex = -1;
        //                            //List<int> trackedBodies = new List<int>(6);

        //                            //foreach (Body body in this.bodies)
        //                            //{
        //                            //    bodyIndex++;

        //                            //    if (body.IsTracked && !countingDown && !takingPicture)
        //                            //    {
        //                            //        trackedBodies.Add(bodyIndex);

        //                            //        // Check our Motion Detector
        //                            //        md.Update(bodyIndex, currentDepth, currentBodyImage);

        //                            //        md.
        //                            //        // Is anyone moving?
        //                            //        anyoneMoving = md.DidPlayerMove();

        //                            //        // Person is moving ->  Update the time of their last movement
        //                            //        if (anyoneMoving)
        //                            //        {
        //                            //            trackedMovedTime[bodyIndex] = DateTime.Now;
        //                            //        }

        //                            //        // Person not moving -> Check the time gap since last movement and start timer if they have been still long enough
        //                            //        else
        //                            //        {
        //                            //            TimeSpan timeSinceLastMove = DateTime.Now.Subtract(trackedMovedTime[bodyIndex]);
        //                            //            string msg = "Time since last movement for Player " + bodyIndex + ": " + timeSinceLastMove.TotalMilliseconds.ToString();
        //                            //            //this.statusMessage.Text = msg;
        //                            //            Debug.WriteLine(msg);

        //                            //            if (timeSinceLastMove.TotalMilliseconds >= requiredStillTime)
        //                            //            {
        //                            //                // Set our flag that we are in the process of taking a picture
        //                            //                //takingPicture = true;
        //                            //                // Only show the timer if we don't have it up
        //                            //                if (!countingDown)
        //                            //                {
        //                            //                    // Start our countdown timer
        //                            //                    countingDown = true;
        //                            //                    this.timerCountdown.Visibility = Visibility.Visible;
        //                            //                    this.timerCountdown.StartCountdownAsync(this.timerLength);
        //                            //                    //return;
        //                            //                }

        //                            //            }
        //                            //            else
        //                            //            {
        //                            //                //countingDown
        //                            //                Log("Still moving...");
        //                            //            }
        //                            //        }
        //                            //    }
        //                            //}
        //                        }
        //                        else
        //                        {
        //                            //this.helpTips.Text = "Say \'Cheese\' to take a picture\n\nSay \'Start Timer\' to countdown";
        //                        }
        //                    }
        //                }
        //            }
        //        }
        //    }
        //}

    }
}
