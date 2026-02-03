using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local; // Package installed, this should work now
using Sdcb.PaddleInference;
using OpenCvSharp;

namespace MyTimestamp
{
    public class PaddleOcrService : IOcrService
    {
        public string Name => "PaddleOCR (Local)";
        public bool IsAvailable => true; // Always available if libs are present

        private PaddleOcrAll? _engine;

        public async Task InitAsync()
        {
            if (_engine != null) return;

            await Task.Run(() =>
            {
                // Full model (Detection + Classification + Recognition)
                // Using LocalV3 standard models (English/Chinese usually default, check args)
                // "LocalV3" usually includes multiple languages in the nuget or you specify.
                // Assuming "English" is needed. LocalV3 default might be Chinese/English mixed or universal.
                // Let's use KnownOCRModel.EnglishV3 if accessible, or just default which is often robust.
                
                // Note: The nuget "Sdcb.PaddleOCR.Models.Local" provides `LocalFullModels`.
                
                _engine = new PaddleOcrAll(LocalFullModels.EnglishV3, PaddleDevice.Mkldnn())
                {
                    AllowRotateDetection = true,
                    Enable180Classification = false,
                };
            });
        }

        public async Task<OcrResultModel> RecognizeAsync(System.Drawing.Bitmap image)
        {
            if (_engine == null) await InitAsync();
            if (_engine == null) throw new Exception("PaddleOCR Engine not initialized.");

            return await Task.Run(() =>
            {
                // Convert System.Drawing.Bitmap to OpenCvSharp.Mat
                // This is needed because PaddleOCR uses OpenCvSharp
                using (var ms = new MemoryStream())
                {
                    image.Save(ms, ImageFormat.Bmp);
                    ms.Position = 0;
                    
                    // Decode from stream to Mat
                    using (var mat = Cv2.ImDecode(Mat.FromStream(ms, ImreadModes.Color), ImreadModes.Color))
                    {
                        var result = _engine.Run(mat);
                        
                        // Result text
                        return new OcrResultModel
                        {
                            Text = result.Text,
                            Confidence = 0 // Aggregate confidence if needed, result.Regions[i].Score
                        };
                    }
                }
            });
        }
    }
}
