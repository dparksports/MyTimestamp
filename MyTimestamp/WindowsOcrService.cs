using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MyTimestamp
{
    public class WindowsOcrService : IOcrService
    {
        public string Name => "Windows Native";
        public bool IsAvailable => true;
        
        private OcrEngine? _engine;

        public Task InitAsync()
        {
            // WinRT OCR initialization is synchronous basically, mostly just CheckAvailable
            if (_engine != null) return Task.CompletedTask;

            try 
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine == null)
                {
                    // Fallback to English if possible?
                    // Typically TryCreateFromUserProfileLanguages returns null if no valid language pack.
                    // Let's try explicit english if user doesn't have it? 
                    // Usually users have their own language.
                    throw new Exception("Could not create Windows OCR Engine. Ensure a Language Pack is installed.");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Windows OCR Init Failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public async Task<OcrResultModel> RecognizeAsync(Bitmap image)
        {
             if (_engine == null) await InitAsync();
             if (_engine == null) throw new Exception("Windows OCR Engine is null.");

             // Convert System.Drawing.Bitmap to SoftwareBitmap
             // Why? Because our IOcrService takes System.Drawing.Bitmap (common ground for Tesseract)
             // But Windows OCR needs SoftwareBitmap.
             
             // 1. Bitmap -> Stream -> RandomAccessStream -> Decoder -> SoftwareBitmap
             // This is a bit inefficient but cleanly separates the logic.
             // Since we already convert to WinForms Bitmap for Preprocessing, this is fine.
             
             SoftwareBitmap? sb = null;
             
             using (var ms = new MemoryStream())
             {
                 image.Save(ms, ImageFormat.Png);
                 ms.Position = 0;
                 
                 // Create RandomAccessStream from MemoryStream
                 // We need a helper or use AsRandomAccessStream from System.Runtime.InteropServices.WindowsRuntime
                 // But wait, we can just use the BitmapDecoder.
                 
                 var randomAccessStream = ms.AsRandomAccessStream();
                 var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                 sb = await decoder.GetSoftwareBitmapAsync();
             }

             if (sb == null) return new OcrResultModel();

             // Ensure format
             if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
             {
                 sb = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
             }

             var result = await _engine.RecognizeAsync(sb);
             
             return new OcrResultModel
             {
                 Text = result.Text,
                 Confidence = 0.0 // Windows OCR doesn't give global confidence easily
             };
        }
    }
}
