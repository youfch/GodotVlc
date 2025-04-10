using System;
using System.Runtime.InteropServices;
using Godot;
using LibVLCSharp.Shared;

namespace libVLCsolution.Scripts
{
    /// <summary>
    /// The main class of the video player application.
    /// Responsible for initializing the VLC media player, controlling video playback, and releasing resources.
    /// </summary>
    public partial class App : Node
    {
        // VLC-related components
        private LibVLC libVLC;                  // VLC library instance
        private MediaPlayer mediaPlayer;         // Media player instance
        private Media media;                     // The currently playing media

        // Video texture-related
        private ImageTexture videoTexture;       // The texture used to display the video
        private byte[] videoBuffer;             // Video frame buffer
        private GCHandle videoBufferHandle;      // The GC handle of the video buffer
        private int videoWidth;                 // Video width
        private int videoHeight;                // Video height

        private Image image;                    // The image used to create the video texture

        // UI components
        [Export] private Button Play;
        [Export] private Button PauseContinue;
        [Export] private HSlider Progress;

        [Export] private LineEdit VidPathEdit;   // Video path input box
        [Export] private TextureRect videoDisplay; // Video display area
        [Export] private OptionButton PlaybackSpeed; // Dropdown for selecting playback speed

        private bool isVideoPlaying = false;     // Video playback status flag
        private float currentPlaybackRate = 1.0f; // Current playback speed

        /// <summary>
        /// Initialize VLC components and video callbacks when the node is ready.
        /// </summary>
        public override void _Ready()
        {
            base._Ready();

            Core.Initialize();

            // Configure VLC options to optimize online video playback
            string[] vlcOptions = {
                "--network-caching=3000",      // Network caching time (in milliseconds)
                "--live-caching=1500",        // Live stream caching time
                "--sout-mux-caching=1500",   // Output caching time
                "--http-reconnect",          // Enable HTTP reconnection
                "--adaptive-maxwidth=1920",   // Maximum video width
                "--adaptive-maxheight=1080"  // Maximum video height
            };
            libVLC = new LibVLC(vlcOptions);

            mediaPlayer = new MediaPlayer(libVLC);
            mediaPlayer.EnableHardwareDecoding = true;

            videoTexture = new ImageTexture();
            image = new Image();

            mediaPlayer.SetVideoFormatCallbacks(VideoFormat, CleanupVideo);
            mediaPlayer.SetVideoCallbacks(LockVideo, UnlockVideo, DisplayVideo);

            // Initialize playback speed options
            InitializePlaybackSpeedOptions();

            // Bind button events
            PauseContinue.Toggled += (b) => {
                TogglePlayPause(!b);
                if (b)
                { PauseContinue.Text = "Continue"; }
                else
                { PauseContinue.Text = "Pause"; }
            };
            Progress.ValueChanged += OnProgressValueChanged;
            PlaybackSpeed.ItemSelected += OnPlaybackSpeedChanged;
        }

        /// <summary>
        /// Initialize the dropdown for selecting playback speed.
        /// </summary>
        private void InitializePlaybackSpeedOptions()
        {
            if (PlaybackSpeed != null)
            {
                PlaybackSpeed.Clear();
                PlaybackSpeed.AddItem("0.5x", 0);
                PlaybackSpeed.AddItem("0.75x", 1);
                PlaybackSpeed.AddItem("1.0x", 2);
                PlaybackSpeed.AddItem("1.25x", 3);
                PlaybackSpeed.AddItem("1.5x", 4);
                PlaybackSpeed.AddItem("2.0x", 5);

                // Select 1.0x by default
                PlaybackSpeed.Selected = 2;
            }
        }

        /// <summary>
        /// Handle the event when the playback speed selection changes.
        /// </summary>
        private void OnPlaybackSpeedChanged(long index)
        {
            if (mediaPlayer == null) return;

            float[] speedValues = { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f };
            if (index >= 0 && index < speedValues.Length)
            {
                float newRate = speedValues[index];
                SetPlaybackSpeed(newRate);
            }
        }

        /// <summary>
        /// Set the playback speed.
        /// </summary>
        /// <param name="rate">Playback speed multiplier</param>
        public void SetPlaybackSpeed(float rate)
        {
            if (mediaPlayer != null)
            {
                currentPlaybackRate = rate;
                mediaPlayer.SetRate(rate);
                GD.Print($"Playback speed set to {rate}x");
            }
        }

        /// <summary>
        /// Handle the media state change event.
        /// Release related resources when the video playback ends.
        /// </summary>
        private void MediaStateChangedHandler(object o, MediaStateChangedEventArgs mediaState)
        {
            GD.Print($"State: {mediaState.State}");
            if (mediaState.State == VLCState.Ended)
            {
                GD.Print("Video ended");
                FreeMedia();
            }
        }

        /// <summary>
        /// Start playing the video.
        /// Supports both local files and HTTP streaming links.
        /// </summary>
        public void StartVideo()
        {
            string playPath = VidPathEdit.Text.Replace("\\", "\\\\");

            // Check if it is an online video URL
            bool isOnlineVideo = playPath.StartsWith("http") || playPath.StartsWith("https") || playPath.StartsWith("rtsp");

            if (isOnlineVideo)
            {
                if (!Uri.TryCreate(playPath, UriKind.Absolute, out Uri uriResult) ||
                    (uriResult.Scheme != Uri.UriSchemeHttp &&
                     uriResult.Scheme != Uri.UriSchemeHttps &&
                     uriResult.Scheme != "rtsp"))
                {
                    GD.PrintErr("Invalid video URL: ", playPath);
                    return;
                }
            }
            else if (!FileAccess.FileExists(playPath))
            {
                GD.PrintErr("Video file not found: ", playPath);
                return;
            }

            mediaPlayer?.Stop();
            FreeMedia();

            try
            {
                media = new Media(libVLC, new Uri(playPath));

                // Set network-related options
                media.AddOption(":network-caching=3000");
                media.AddOption(":clock-jitter=0");
                media.AddOption(":clock-synchro=0");

                // Register event handlers
                media.StateChanged += MediaStateChangedHandler;
                media.ParsedChanged += (sender, e) => GD.Print("Media parsed state changed: ", e);

                // Start parsing the media
                var parseResult = media.Parse(MediaParseOptions.ParseNetwork);

                mediaPlayer.Play(media);
                isVideoPlaying = true;

                GD.Print("Starting video playback: ", playPath);
            }
            catch (Exception ex)
            {
                GD.PrintErr("Error starting video: ", ex.Message);
                FreeMedia();
            }
        }

        /// <summary>
        /// Set the video format callback.
        /// Configure the format, size, and buffer of the video frame.
        /// </summary>
        private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
        {
            string chromaFormat = "RGBA";
            byte[] chromaBytes = System.Text.Encoding.ASCII.GetBytes(chromaFormat);
            Marshal.Copy(chromaBytes, 0, chroma, chromaBytes.Length);

            videoWidth = (int)width;
            videoHeight = (int)height;

            pitches = (uint)(videoWidth * 4);
            lines = (uint)videoHeight;

            if (videoBuffer == null)
            {
                videoBuffer = new byte[videoWidth * videoHeight * 4]; // RGBA buffer
                videoBufferHandle = GCHandle.Alloc(videoBuffer, GCHandleType.Pinned);
            }

            image.SetData(videoWidth, videoHeight, false, Image.Format.Rgba8, videoBuffer);

            if (videoTexture != null)
            {
                videoTexture.Dispose();
            }

            videoTexture = ImageTexture.CreateFromImage(image);
            CallDeferred(nameof(UpdateTexture), videoTexture);

            return 1;
        }

        /// <summary>
        /// Update the video display texture.
        /// </summary>
        private void UpdateTexture(ImageTexture texture)
        {
            if (videoDisplay != null)
            {
                videoDisplay.Texture = texture;
            }
        }

        /// <summary>
        /// Clean up video resources.
        /// Release the buffer and texture.
        /// </summary>
        private void CleanupVideo(ref IntPtr opaque)
        {
            if (videoBufferHandle.IsAllocated)
            {
                videoBufferHandle.Free();
            }

            videoBuffer = null;
            videoTexture?.Dispose();
            videoTexture = null;
        }

        /// <summary>
        /// Lock the video frame buffer.
        /// Return the buffer address for VLC to write data.
        /// </summary>
        private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, videoBufferHandle.AddrOfPinnedObject());
            return IntPtr.Zero;
        }

        /// <summary>
        /// Unlock the video frame buffer.
        /// </summary>
        private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes) { }

        private float lastUpdateTime = 0f;        // Last update time
        private const float updateInterval = 1f / 30f; // Update interval (30 fps)

        /// <summary>
        /// The callback for displaying the video frame.
        /// Control the video update frequency to avoid overly frequent updates.
        /// </summary>
        private void DisplayVideo(IntPtr opaque, IntPtr picture)
        {
            float currentTime = Time.GetTicksMsec() / 1000f;
            if (currentTime - lastUpdateTime >= updateInterval)
            {
                lastUpdateTime = currentTime;
                CallDeferred(nameof(UpdateDisplayVideo), videoTexture);
            }
        }

        /// <summary>
        /// Update the video display.
        /// Update the new video frame data into the texture.
        /// </summary>
        private void UpdateDisplayVideo(ImageTexture texture)
        {
            if (videoTexture != null && videoBuffer != null)
            {
                image.SetData(videoWidth, videoHeight, false, Image.Format.Rgba8, videoBuffer);
                videoTexture.Update(image);
            }
        }

        /// <summary>
        /// Release media resources.
        /// Clean up the currently playing media and unbind events.
        /// </summary>
        private void FreeMedia()
        {
            if (media != null)
            {
                media.StateChanged -= MediaStateChangedHandler;
                media.Dispose();
                media = null;
            }
        }

        /// <summary>
        /// Cleanup work when the node exits the tree.
        /// Release all VLC-related resources and the video buffer.
        /// </summary>
        public override void _ExitTree()
        {
            try
            {
                // Stop playing
                if (mediaPlayer != null && mediaPlayer.IsPlaying)
                {
                    mediaPlayer.Stop();
                }

                // Release media resources
                FreeMedia();

                // Release the player instance
                if (mediaPlayer != null)
                {
                    mediaPlayer.SetVideoCallbacks(null, null, null);
                    mediaPlayer.SetVideoFormatCallbacks(null, null);
                    mediaPlayer.Dispose();
                    mediaPlayer = null;
                }

                // Release video buffer resources
                if (videoBufferHandle.IsAllocated)
                {
                    videoBufferHandle.Free();
                }
                videoBuffer = null;

                // Release the video texture
                if (videoTexture != null)
                {
                    videoTexture.Dispose();
                    videoTexture = null;
                }

                // Release the image resources
                if (image != null)
                {
                    image.Dispose();
                    image = null;
                }

                // Finally, release the VLC library instance
                if (libVLC != null)
                {
                    libVLC.Dispose();
                    libVLC = null;
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("Error during cleanup: ", ex.Message);
            }
        }

        /// <summary>
        /// Pause or resume the video playback.
        /// </summary>
        /// <param name="play">true for play, false for pause</param>
        public void TogglePlayPause(bool play)
        {
            if (mediaPlayer == null || media == null) return;

            if (play && !isVideoPlaying)
            {
                mediaPlayer.Play();
                isVideoPlaying = true;
            }
            else if (!play && isVideoPlaying)
            {
                mediaPlayer.Pause();
                isVideoPlaying = false;
            }
        }

        /// <summary>
        /// Pause the video playback.
        /// </summary>
        public void PauseVideo()
        {
            if (mediaPlayer != null && media != null && isVideoPlaying)
            {
                mediaPlayer.Pause();
                isVideoPlaying = false;
            }
        }

        /// <summary>
        /// Resume the video playback.
        /// </summary>
        public void ResumeVideo()
        {
            if (mediaPlayer != null && media != null && !isVideoPlaying)
            {
                mediaPlayer.Play();
                isVideoPlaying = true;
            }
        }

        /// <summary>
        /// Get the total length of the current video (in milliseconds).
        /// </summary>
        /// <returns>The length of the video (in milliseconds), returns 0 if no video is playing.</returns>
        public int GetVideoLength()
        {
            if (mediaPlayer == null || media == null)
                return 0;

            return (int)mediaPlayer.Length;
        }

        /// <summary>
        /// Get the formatted string of the total length of the current video (in hours:minutes:seconds).
        /// </summary>
        /// <returns>The formatted time string, returns "00:00:00" if no video is playing.</returns>
        public string GetFormattedVideoLength()
        {
            long totalMilliseconds = GetVideoLength();
            if (totalMilliseconds <= 0)
                return "00:00:00";

            TimeSpan time = TimeSpan.FromMilliseconds(totalMilliseconds);
            return time.ToString(@"hh\:mm\:ss");
        }

        /// <summary>
        /// Handle the event when the value of the progress bar changes.
        /// </summary>
        private void OnProgressValueChanged(double value)
        {
            if (mediaPlayer != null && media != null)
            {
                // Convert the value of the progress bar (0-100) to video time (in milliseconds)
                long position = (long)(value * mediaPlayer.Length / 100.0);
                mediaPlayer.Time = position;
            }
        }
    }
}