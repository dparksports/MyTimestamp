using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Tesseract;

namespace MyTimestamp
{
    public class TesseractOcrService : IOcrService
    {
        public string Name => "Tesseract OCR";
        public bool IsAvailable => true; // Always available if installed, but Init might fail if data missing

        private TesseractEngine? _engine;
        private const string TessDataPath = "tessdata";
        private const string Lang = "eng";

        public async Task InitAsync()
        {
            if (_engine != null) return;

            // Check for tessdata
            if (!Directory.Exists(TessDataPath))
            {
                Directory.CreateDirectory(TessDataPath);
            }

            string dataFile = Path.Combine(TessDataPath, $"{Lang}.traineddata");
            if (!File.Exists(dataFile))
            {
                // Download fast model
                await DownloadTessDataAsync(dataFile);
            }

            try
            {
                _engine = new TesseractEngine(TessDataPath, Lang, EngineMode.Default);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to initialize Tesseract: {ex.Message}");
            }
        }

        private async Task DownloadTessDataAsync(string destination)
        {
            string url = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata";
            using (var client = new HttpClient())
            {
                var data = await client.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(destination, data);
            }
        }

        public Task<OcrResultModel> RecognizeAsync(Bitmap image)
        {
            return Task.Run(() =>
            {
                if (_engine == null) throw new InvalidOperationException("Tesseract Engine not initialized.");

                using (var ms = new MemoryStream())
                {
                    image.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                    ms.Position = 0;
                    
                    using (var pix = Pix.LoadFromMemory(ms.ToArray()))
                    {
                        using (var page = _engine.Process(pix))
                        {
                            var text = page.GetText();
                            var confidence = page.GetMeanConfidence();
                            
                            return new OcrResultModel
                            {
                                Text = text,
                                Confidence = confidence
                            };
                        }
                    }
                }
            });
        }
    }
}
