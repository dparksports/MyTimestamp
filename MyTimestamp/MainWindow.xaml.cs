using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives; // For Thumb
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Linq;
using Windows.Media.Editing;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MyTimestamp
{
    public partial class MainWindow : Window
    {
        private bool _isDraggingSlider = false;
        private OcrEngine? _ocrEngine;
        
        // Services
        private IOcrService _currentOcrService;
        private WindowsOcrService _winOcr = new WindowsOcrService();
        private TesseractOcrService _tessOcr = new TesseractOcrService();
        private PaddleOcrService _paddleOcr = new PaddleOcrService();

        // ROI Dragging State
        private bool _isDraggingRoi = false;
        private Point _roiClickOffset;
        
        private string? _currentVideoPath;

        public MainWindow()
        {
            InitializeComponent();
            
            // Default
            _currentOcrService = _winOcr;
            InitializeOcr();
        }

        private async void InitializeOcr()
        {
            try
            {
                await _currentOcrService.InitAsync();
                
                // Legacy for AutoFind
                try {
                     if (_ocrEngine == null) _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                } catch {}
            }
            catch (Exception ex)
            {
               MessageBox.Show($"Error initializing OCR: {ex.Message}");
            }
        }

        private void OpenVideo_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv|All Files|*.*"
            };

            if (ofd.ShowDialog() == true)
            {
                _currentVideoPath = ofd.FileName;
                VideoPlayer.Source = new Uri(ofd.FileName);
                VideoPlayer.Play();
                VideoPlayer.Pause(); 
            }
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                ScrubBar.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                TotalTimeText.Text = VideoPlayer.NaturalDuration.TimeSpan.ToString(@"hh\:mm\:ss");
            }
            UpdateRoiVisuals();
        }

        private void ScrubBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isDraggingSlider) 
            {
                VideoPlayer.Position = TimeSpan.FromSeconds(ScrubBar.Value);
                CurrentTimeText.Text = VideoPlayer.Position.ToString(@"hh\:mm\:ss");
            }
        }
        
        private void ScrubBar_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void ScrubBar_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isDraggingSlider = false;
            VideoPlayer.Position = TimeSpan.FromSeconds(ScrubBar.Value);
            CurrentTimeText.Text = VideoPlayer.Position.ToString(@"hh\:mm\:ss");
        }

        // =========================================================
        // ROI Drag & Resize Logic
        // =========================================================

        private void Roi_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Only update visuals if we are NOT currently dragging/resizing from the UI 
            // (to avoid circular update loops or fighting, though basic binding usually works ok).
            // For now, simple implementation:
            UpdateRoiVisuals();
        }

        private void UpdateRoiPreview_Click(object sender, RoutedEventArgs e)
        {
             UpdateRoiVisuals();
        }

        private void UpdateRoiVisuals()
        {
            if (VideoContainer.ActualWidth == 0 || VideoContainer.ActualHeight == 0) return;

            // This update is from TEXTBOXES -> VISUALS
            if (double.TryParse(RoiX.Text, out double x) &&
                double.TryParse(RoiY.Text, out double y) &&
                double.TryParse(RoiW.Text, out double w) &&
                double.TryParse(RoiH.Text, out double h))
            {
                double pixelX = x * VideoContainer.ActualWidth;
                double pixelY = y * VideoContainer.ActualHeight;
                double pixelW = w * VideoContainer.ActualWidth;
                double pixelH = h * VideoContainer.ActualHeight;

                Canvas.SetLeft(RoiGrid, pixelX);
                Canvas.SetTop(RoiGrid, pixelY);
                RoiGrid.Width = Math.Max(0, pixelW);
                RoiGrid.Height = Math.Max(0, pixelH);
            }
        }

        // --- Dragging the ROI (Move) ---

        private void Roi_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingRoi = true;
            RoiGrid.CaptureMouse();
            _roiClickOffset = e.GetPosition(RoiGrid); // Offset within the grid
        }

        private void Roi_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingRoi)
            {
                var canvasPos = e.GetPosition(OverlayCanvas);
                // TopLeft of ROI = CanvasPos - Offset
                double newLeft = canvasPos.X - _roiClickOffset.X;
                double newTop = canvasPos.Y - _roiClickOffset.Y;

                // Boundary Checks
                if (newLeft < 0) newLeft = 0;
                if (newTop < 0) newTop = 0;
                if (newLeft + RoiGrid.ActualWidth > OverlayCanvas.ActualWidth) newLeft = OverlayCanvas.ActualWidth - RoiGrid.ActualWidth;
                if (newTop + RoiGrid.ActualHeight > OverlayCanvas.ActualHeight) newTop = OverlayCanvas.ActualHeight - RoiGrid.ActualHeight;

                Canvas.SetLeft(RoiGrid, newLeft);
                Canvas.SetTop(RoiGrid, newTop);

                // Sync back to TextBoxes
                UpdateTextBoxesFromVisuals();
            }
        }

        private void Roi_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingRoi)
            {
                _isDraggingRoi = false;
                RoiGrid.ReleaseMouseCapture();
                UpdateTextBoxesFromVisuals();
            }
        }

        // --- Resizing the ROI ---

        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var thumb = sender as Thumb;
            if (thumb == null) return;
            string tag = thumb.Tag as string ?? "";

            double left = Canvas.GetLeft(RoiGrid);
            double top = Canvas.GetTop(RoiGrid);
            double width = RoiGrid.ActualWidth;
            double height = RoiGrid.ActualHeight;

            if (Double.IsNaN(left)) left = 0;
            if (Double.IsNaN(top)) top = 0;

            // Calculate new dimensions based on which thumb is dragged
            if (tag.Contains("Left"))
            {
                double change = e.HorizontalChange;
                // Don't shrink below min width (e.g. 10)
                if (width - change < 10) change = width - 10;
                
                // Don't expand past left edge of container
                if (left + change < 0) change = -left;

                left += change;
                width -= change;
            }
            else if (tag.Contains("Right"))
            {
                double change = e.HorizontalChange;
                if (width + change < 10) change = 10 - width;
                if (left + width + change > OverlayCanvas.ActualWidth) change = OverlayCanvas.ActualWidth - (left + width);

                width += change;
            }

            if (tag.Contains("Top"))
            {
                double change = e.VerticalChange;
                if (height - change < 10) change = height - 10;
                if (top + change < 0) change = -top;

                top += change;
                height -= change;
            }
            else if (tag.Contains("Bottom"))
            {
                double change = e.VerticalChange;
                if (height + change < 10) change = 10 - height;
                if (top + height + change > OverlayCanvas.ActualHeight) change = OverlayCanvas.ActualHeight - (top + height);

                height += change;
            }

            // Apply
            Canvas.SetLeft(RoiGrid, left);
            Canvas.SetTop(RoiGrid, top);
            RoiGrid.Width = width;
            RoiGrid.Height = height;

            UpdateTextBoxesFromVisuals();
        }

        private void UpdateTextBoxesFromVisuals()
        {
            if (VideoContainer.ActualWidth == 0 || VideoContainer.ActualHeight == 0) return;

            double x = Canvas.GetLeft(RoiGrid) / VideoContainer.ActualWidth;
            double y = Canvas.GetTop(RoiGrid) / VideoContainer.ActualHeight;
            double w = RoiGrid.ActualWidth / VideoContainer.ActualWidth;
            double h = RoiGrid.ActualHeight / VideoContainer.ActualHeight;

            // Avoid infinite loops by temporarily detaching handlers or just checking values?
            // Simple rounding to strings avoids tiny float jitter triggering changes.
            RoiX.Text = Math.Round(x, 4).ToString();
            RoiY.Text = Math.Round(y, 4).ToString();
            RoiW.Text = Math.Round(w, 4).ToString();
            RoiH.Text = Math.Round(h, 4).ToString();
        }

        // =========================================================
        // OCR Logic
        // =========================================================

        private async void ComboOcrEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (ComboOcrEngine.SelectedIndex == 0)
                    _currentOcrService = _winOcr;
                else if (ComboOcrEngine.SelectedIndex == 1)
                    _currentOcrService = _tessOcr;
                else
                    _currentOcrService = _paddleOcr;

                await _currentOcrService.InitAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize OCR Engine: {ex.Message}");
                // Revert to Windows Native if failed (e.g. AI not supported)
                ComboOcrEngine.SelectedIndex = 0;
            }
        }

        // Preprocessing UI Handlers
        private void Preprocessing_Changed(object sender, RoutedEventArgs e)
        {
            // Auto-update preview window if open? For now just ready for next click.
        }
        
        private async void PreviewImage_Click(object sender, RoutedEventArgs e)
        {
             if (string.IsNullOrEmpty(_currentVideoPath)) return;
             
             // 1. Capture High Res
             var fullBmp = await CaptureHighResFrame(_currentVideoPath, VideoPlayer.Position);
             if (fullBmp == null) return;
             
             // 2. Crop
              if (!double.TryParse(RoiX.Text, out double x) ||
                    !double.TryParse(RoiY.Text, out double y) ||
                    !double.TryParse(RoiW.Text, out double w) ||
                    !double.TryParse(RoiH.Text, out double h)) return;

                int cropX = (int)(x * fullBmp.PixelWidth);
                int cropY = (int)(y * fullBmp.PixelHeight);
                int cropW = (int)(w * fullBmp.PixelWidth);
                int cropH = (int)(h * fullBmp.PixelHeight);

                // Boundary/Crash checks
                if (cropX < 0) cropX = 0; if (cropY < 0) cropY = 0;
                if (cropX + cropW > fullBmp.PixelWidth) cropW = fullBmp.PixelWidth - cropX;
                if (cropY + cropH > fullBmp.PixelHeight) cropH = fullBmp.PixelHeight - cropY;
                if (cropW <= 0 || cropH <= 0) return;

                var wpfBitmap = await ConvertSoftwareBitmapToWpf(fullBmp);
                var cropped = new CroppedBitmap(wpfBitmap, new Int32Rect(cropX, cropY, cropW, cropH));
             
             // 3. Process
             var processed = ApplyPreprocessing(cropped);
             
             // 4. Show in new Window
             var previewWindow = new Window
             {
                 Title = "Processed Image Preview",
                 Width = processed.PixelWidth + 40,
                 Height = processed.PixelHeight + 60,
                 Content = new Image { Source = processed, Stretch = Stretch.Uniform }
             };
             previewWindow.ShowDialog();
        }

        private async void SaveRoiImage_Click(object sender, RoutedEventArgs e)
        {
             if (string.IsNullOrEmpty(_currentVideoPath)) return;
             
             try 
             {
                 // 1. Capture High Res
                 var fullBmp = await CaptureHighResFrame(_currentVideoPath, VideoPlayer.Position);
                 if (fullBmp == null) return;
                 
                 // 2. Crop
                 if (!double.TryParse(RoiX.Text, out double x) ||
                     !double.TryParse(RoiY.Text, out double y) ||
                     !double.TryParse(RoiW.Text, out double w) ||
                     !double.TryParse(RoiH.Text, out double h)) return;

                 int cropX = (int)(x * fullBmp.PixelWidth);
                 int cropY = (int)(y * fullBmp.PixelHeight);
                 int cropW = (int)(w * fullBmp.PixelWidth);
                 int cropH = (int)(h * fullBmp.PixelHeight);

                 // Boundary checks
                 if (cropX < 0) cropX = 0; if (cropY < 0) cropY = 0;
                 if (cropX + cropW > fullBmp.PixelWidth) cropW = fullBmp.PixelWidth - cropX;
                 if (cropY + cropH > fullBmp.PixelHeight) cropH = fullBmp.PixelHeight - cropY;
                 if (cropW <= 0 || cropH <= 0) return;

                 var wpfBitmap = await ConvertSoftwareBitmapToWpf(fullBmp);
                 var cropped = new CroppedBitmap(wpfBitmap, new Int32Rect(cropX, cropY, cropW, cropH));
                 
                 // 3. Process
                 var processed = ApplyPreprocessing(cropped);
                 
                 // 4. Save
                 var dialog = new Microsoft.Win32.SaveFileDialog
                 {
                     Filter = "PNG Image|*.png|JPEG Image|*.jpg",
                     FileName = $"roi_preview_{DateTime.Now:yyyyMMdd_HHmmss}.png"
                 };

                 if (dialog.ShowDialog() == true)
                 {
                     using (var fileStream = new FileStream(dialog.FileName, FileMode.Create))
                     {
                         System.Windows.Media.Imaging.BitmapEncoder encoder;
                         if (System.IO.Path.GetExtension(dialog.FileName).ToLower() == ".jpg")
                             encoder = new PngBitmapEncoder(); // Fallback to PNG for quality
                         else
                             encoder = new PngBitmapEncoder();

                         encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(processed));
                         encoder.Save(fileStream);
                     }
                 }
             }
             catch (Exception ex)
             {
                 MessageBox.Show($"Failed to save image: {ex.Message}");
             }
        }

        private BitmapSource ApplyPreprocessing(BitmapSource input)
        {
            // Convert to System.Drawing.Bitmap
            using (var winWin = ImageProcessor.ToWinFormsBitmap(input))
            {
               // Process
               using (var processed = ImageProcessor.Process(
                   winWin, 
                   ChkInvert.IsChecked == true, 
                   ChkBinarize.IsChecked == true, 
                   (int)SliderThreshold.Value, 
                   ChkDilate.IsChecked == true))
               {
                   // Convert back
                   return ImageProcessor.ToWpfBitmap(processed);
               }
            }
        }

        private async void AutoFind_Click(object sender, RoutedEventArgs e)
        {
             if (string.IsNullOrEmpty(_currentVideoPath)) return;
             try 
             {
                 // 1. Capture Full High-Res Frame
                 var fullBmp = await CaptureHighResFrame(_currentVideoPath, VideoPlayer.Position);
                 if (fullBmp == null) return;
                 
                 // Branch based on Service Type
                 if (_currentOcrService is TesseractOcrService)
                 {
                     // Convert to Bitmap
                     var wpfFull = await ConvertSoftwareBitmapToWpf(fullBmp);
                     using (var sysDrawingFull = ImageProcessor.ToWinFormsBitmap(wpfFull))
                     {
                         var res = await _currentOcrService.RecognizeAsync(sysDrawingFull);
                         Regex r = new Regex(@"\d{1,2}[:;.]\d{2}([:;.]\d{2})?");
                         var m = r.Match(res.Text);
                         
                         if (m.Success)
                             MessageBox.Show($"Found timestamp: {m.Value}\n(Auto-ROI placement is currently only supported in Windows Native Mode)");
                         else
                             MessageBox.Show("No timestamp pattern found (Tesseract).");
                     }
                     return;
                 }

                 // --- Windows Native Logic (Preserved for ROI placement) ---
                 
                 // Ensure engine exists
                 if (_ocrEngine == null) _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                 if (_ocrEngine == null) return;

                 // 2. Run OCR on full frame
                 var result = await _ocrEngine.RecognizeAsync(fullBmp);
                 
                 // 3. Find Timestamp Pattern
                 Regex timeRegex = new Regex(@"\d{1,2}[:;.]\d{2}([:;.]\d{2})?");
                 
                 OcrResultText.Text = result.Text; // Debug

                 Windows.Foundation.Rect? bestBox = null;
                 string bestText = "";

                 foreach (var line in result.Lines) 
                 {
                     if (timeRegex.IsMatch(line.Text))
                     {
                         foreach(var word in line.Words)
                         {
                             if (timeRegex.IsMatch(word.Text))
                             {
                                 bestBox = word.BoundingRect;
                                 bestText = word.Text;
                                 break; 
                             }
                         }
                         
                         if (bestBox == null) 
                         {
                              if (timeRegex.IsMatch(line.Text))
                              {
                                   if (line.Text.Length < 20) 
                                   {
                                       bestBox = EncloseWords(line, timeRegex);
                                       bestText = line.Text;
                                   }
                              }
                         }
                     }
                     if (bestBox != null) break; 
                 }

                 if (bestBox != null)
                 {
                     var rect = bestBox.Value;
                     double nativeWidth = fullBmp.PixelWidth;
                     double nativeHeight = fullBmp.PixelHeight;

                     // Add some padding
                     double padding = 5;
                     double finalX = Math.Max(0, rect.X - padding);
                     double finalY = Math.Max(0, rect.Y - padding);
                     double finalW = rect.Width + (padding * 2);
                     double finalH = rect.Height + (padding * 2);

                     RoiX.Text = Math.Round(finalX / nativeWidth, 4).ToString();
                     RoiY.Text = Math.Round(finalY / nativeHeight, 4).ToString();
                     RoiW.Text = Math.Round(finalW / nativeWidth, 4).ToString();
                     RoiH.Text = Math.Round(finalH / nativeHeight, 4).ToString();

                     UpdateRoiVisuals();
                     MessageBox.Show($"Found timestamp: {bestText}");
                 }
                 else
                 {
                     MessageBox.Show("No timestamp pattern found in current frame.");
                 }
             }
             catch(Exception ex)
             {
                 MessageBox.Show($"Auto-Find Failed: {ex.Message}");
             }
        }
        
        private Windows.Foundation.Rect EncloseWords(OcrLine line, Regex r)
        {
             // OcrLine does not have BoundingRect directly. We need to union words.
             Windows.Foundation.Rect? unionRect = null;
             
             foreach(var word in line.Words)
             {
                 if (unionRect == null)
                     unionRect = word.BoundingRect;
                 else
                     unionRect = new Windows.Foundation.Rect(
                         Math.Min(unionRect.Value.X, word.BoundingRect.X),
                         Math.Min(unionRect.Value.Y, word.BoundingRect.Y),
                         0, 0); // Temporary, need to calc width/height correctly.
                     
                     // Correct logic: union minX, minY, maxX, maxY
                     double minX = Math.Min(unionRect.Value.X, word.BoundingRect.X);
                     double minY = Math.Min(unionRect.Value.Y, word.BoundingRect.Y);
                     double maxX = Math.Max(unionRect.Value.X + unionRect.Value.Width, word.BoundingRect.X + word.BoundingRect.Width);
                     double maxY = Math.Max(unionRect.Value.Y + unionRect.Value.Height, word.BoundingRect.Y + word.BoundingRect.Height);
                     
                     unionRect = new Windows.Foundation.Rect(minX, minY, maxX - minX, maxY - minY);
             }
             
             return unionRect ?? new Windows.Foundation.Rect(0,0,0,0);
        }

        private async void RunOCR_Click(object sender, RoutedEventArgs e)
        {
            if (_ocrEngine == null || string.IsNullOrEmpty(_currentVideoPath)) return;

            try 
            {
                // 1. Capture High Res Frame
                var fullBmp = await CaptureHighResFrame(_currentVideoPath, VideoPlayer.Position);
                if (fullBmp == null) return;

                // 2. Crop to ROI (Coordinates are in Percentages, apply to Full-Res)
                if (!double.TryParse(RoiX.Text, out double x) ||
                    !double.TryParse(RoiY.Text, out double y) ||
                    !double.TryParse(RoiW.Text, out double w) ||
                    !double.TryParse(RoiH.Text, out double h))
                {
                    MessageBox.Show("Invalid ROI");
                    return;
                }

                int cropX = (int)(x * fullBmp.PixelWidth);
                int cropY = (int)(y * fullBmp.PixelHeight);
                int cropW = (int)(w * fullBmp.PixelWidth);
                int cropH = (int)(h * fullBmp.PixelHeight);

                // Boundary checks to prevent crash
                if (cropX < 0) cropX = 0;
                if (cropY < 0) cropY = 0;
                if (cropX + cropW > fullBmp.PixelWidth) cropW = fullBmp.PixelWidth - cropX;
                if (cropY + cropH > fullBmp.PixelHeight) cropH = fullBmp.PixelHeight - cropY;
                
                if (cropW <= 0 || cropH <= 0) 
                {
                     OcrResultText.Text = "ROI too small or invalid.";
                     return;
                }

                // Problem: CroppedBitmap works on WPF BitmapSource, but fullBmp is WinRT SoftwareBitmap.
                // We should crop *after* converting or crop the SoftwareBitmap?
                // WinRT SoftwareBitmap doesn't have a direct "Crop" method that returns a new one easily without Buffer manipulation.
                // EASIER: Convert SoftwareBitmap -> WriteableBitmap (WPF) -> CroppedBitmap -> SoftwareBitmap?
                // OR: Just run OCR on the whole frame but restrict the search area? 
                // WinRT OCR doesn't support "Region", it processes the whole bitmap.
                
                // Let's go: SoftwareBitmap -> WPF BitmapSource -> CroppedBitmap -> SoftwareBitmap
                // It's a bit heavy but safe.
                
                var wpfBitmap = await ConvertSoftwareBitmapToWpf(fullBmp);
                CroppedBitmap cropped = new CroppedBitmap(wpfBitmap, new Int32Rect(cropX, cropY, cropW, cropH));
                
                // --- PREPROCESSING ---
                var finalInput = ApplyPreprocessing(cropped);
                
                // 3. Convert to Standard Bitmap for Service
                using (var sysDrawingBitmap = ImageProcessor.ToWinFormsBitmap(finalInput))
                {
                    // 4. Run OCR
                    var result = await _currentOcrService.RecognizeAsync(sysDrawingBitmap);

                    // 5. Display
                    OcrResultText.Text = result.Text;
                    ExtractedTimeText.Text = result.Text.Replace("\n", " ").Trim();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"OCR Failed: {ex.Message}");
            }
        }

        private async Task<SoftwareBitmap?> CaptureHighResFrame(string filePath, TimeSpan position)
        {
             try
             {
                 var file = await StorageFile.GetFileFromPathAsync(filePath);
                 var clip = await MediaClip.CreateFromFileAsync(file);
                 var composition = new MediaComposition();
                 composition.Clips.Add(clip);
                 
                 // Get frame at exact position
                 // GetThumbnailAsync is actually capable of full res if we don't specify small dimensions?
                 // Actually, GetThumbnailAsync(time, width, height, scale)
                 // If we want native, we should check clip video encoding properties first? 
                 // Or just use a very large number?
                 // Better: Use `GetThumbnailAsync` with specific logic.
                 // Actually, there isn't a simple "GetFrame" in MediaComposition that guarantees native resolution easily without knowing it.
                 // Let's peek at properties.
                 
                 var encoding = clip.GetVideoEncodingProperties();
                 int width = (int)encoding.Width;
                 int height = (int)encoding.Height;
                 
                 // Generate "Thumbnail" at full resolution
                 var stream = await composition.GetThumbnailAsync(position, width, height, VideoFramePrecision.NearestFrame);
                 
                 // Decode
                 var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                 return await decoder.GetSoftwareBitmapAsync();
             }
             catch(Exception ex)
             {
                 MessageBox.Show($"High-Res Capture Failed: {ex.Message}");
                 return null;
             }
        }
        
        // Helper to convert back to WPF for cropping
        private async Task<BitmapSource> ConvertSoftwareBitmapToWpf(SoftwareBitmap sb)
        {
             // SoftwareBitmap -> Stream -> BitmapImage
             // Ensure it's Bgra8 and Premultiplied
             if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
             {
                 sb = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
             }
             
             var ms = new InMemoryRandomAccessStream();
             var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ms);
             encoder.SetSoftwareBitmap(sb);
             await encoder.FlushAsync();
             
             ms.Seek(0);
             var stream = ms.AsStreamForRead();
             
             var bitmap = new BitmapImage();
             bitmap.BeginInit();
             bitmap.StreamSource = stream;
             bitmap.CacheOption = BitmapCacheOption.OnLoad;
             bitmap.EndInit();
             
             return bitmap;
        }

        private async Task<SoftwareBitmap> ConvertToSoftwareBitmap(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                ms.Position = 0;
                
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
                var transformStruct = new Windows.Graphics.Imaging.BitmapTransform();
                var sb = await decoder.GetSoftwareBitmapAsync();
                return sb;
            }
        }
    }
}