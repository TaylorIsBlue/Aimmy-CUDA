﻿using Aimmy2.Class;
using Aimmy2.InputLogic;
using Aimmy2.Other;
using Aimmy2.WinformsReplacement;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Win32;
using Supercluster.KDTree;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using Visuality;
using Application = System.Windows.Application;

namespace Aimmy2.AILogic
{
    internal class AIManager : IDisposable
    {
        #region Variables
        private const int NUM_DETECTIONS = 8400; // Standard for OnnxV8 model (Shape: 1x5x8400)
        private const int IMAGE_SIZE = 640;

        private DateTime lastSavedTime = DateTime.MinValue;
        private RectangleF LastDetectionBox;
        private KalmanPrediction kalmanPrediction;
        private WiseTheFoxPrediction wtfpredictionManager;


        //Direct3D Variables
        //private ID3D11Device _device;
        //private ID3D11DeviceContext _context;
        //private IDXGIOutputDuplication _outputDuplication;
        //private ID3D11Texture2D _desktopImage;

        

        private int ScreenWidth = WinAPICaller.ScreenWidth;
        private int ScreenHeight = WinAPICaller.ScreenHeight;


        private Thread? _aiLoopThread;
        private bool _isAiLoopRunning;

        //fps - copilot, rolling average calculation
        private const int MAXSAMPLES = 100;
        private double[] frameTimes = new double[MAXSAMPLES];
        private int frameTimeIndex = 0;
        private double totalFrameTime = 0;

        // For Auto-Labelling Data System
        //private bool PlayerFound = false;

        private double CenterXTranslated = 0;
        private double CenterYTranslated = 0;

        // For Shall0e's Prediction Method
        private int PrevX = 0;

        private int PrevY = 0;

        // private int IndependentMousePress = 0;

        private int iterationCount = 0;
        private long totalTime = 0;

        private int detectedX { get; set; }
        private int detectedY { get; set; }

        public double AIConf = 0;
        private static int targetX, targetY;

        //private Graphics? _graphics;

        private readonly ModelManager _modelManager = new();
        private readonly CaptureManager _captureManager = new();


        #endregion Variables

        public AIManager(string modelPath)
        {
            kalmanPrediction = new KalmanPrediction();
            wtfpredictionManager = new WiseTheFoxPrediction();

            SystemEvents.DisplaySettingsChanged += (s, e) => _captureManager.DisplaySettingsChanged();

            // Attempt to load via CUDA (else fallback to CPU)
            Task.Run(() => _modelManager.InitializeModel(modelPath));

            // load ai loop after model is loaded
            _isAiLoopRunning = true;
            _aiLoopThread = new Thread(AiLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _aiLoopThread.Start();


            if (Dictionary.dropdownState["Screen Capture Method"] == "DirectX")
            {
                _captureManager.InitializeDirectX();
            }
        }
        #region AI

        private static bool ShouldPredict() => Dictionary.toggleState["Show Detected Player"] || Dictionary.toggleState["Constant AI Tracking"] || InputBindingManager.IsHoldingBinding("Aim Keybind") || InputBindingManager.IsHoldingBinding("Second Aim Keybind");
        private static bool ShouldProcess() => Dictionary.toggleState["Aim Assist"] || Dictionary.toggleState["Show Detected Player"] || Dictionary.toggleState["Auto Trigger"];

        private void UpdateFps(double newFrameTime)
        {
            totalFrameTime += newFrameTime - frameTimes[frameTimeIndex];
            frameTimes[frameTimeIndex] = newFrameTime;

            if (++frameTimeIndex >= MAXSAMPLES)
            {
                frameTimeIndex = 0;
            }
        }

        private async void AiLoop()
        {
            Stopwatch stopwatch = new();
            DetectedPlayerWindow? DetectedPlayerOverlay = Dictionary.DetectedPlayerOverlay;

            stopwatch.Start();
            while (_isAiLoopRunning)
            {

                if (Dictionary.toggleState["Show FPS"])
                {
                    double frameTime = stopwatch.Elapsed.TotalSeconds;
                    UpdateFps(frameTime);
                    if (frameTimeIndex % 10 == 0)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (DetectedPlayerOverlay != null) DetectedPlayerOverlay.FpsLabel.Content = $"FPS: {MAXSAMPLES / totalFrameTime:F2}"; // turn on esp, FPS usually is around 30 fps.
                        });
                    }
                }

                stopwatch.Restart();

                UpdateFOV();

                if (iterationCount == 1000)
                {
                    if (Dictionary.toggleState["Debug Mode"])
                    {
                        double averageTime = totalTime / 1000.0;
                        FileManager.LogInfo($"Average loop iteration time: {averageTime} ms", true);
                        totalTime = 0;
                        iterationCount = 0;
                    }
                }

                float scaleX = ScreenWidth / 640f; // on new resolution you would need to restart the ailoop by loading new model or restarting the app
                float scaleY = ScreenHeight / 640f;
                if (ShouldProcess())
                {
                    if (ShouldPredict())
                    {
                        var closestPrediction = await GetClosestPrediction();
                        if (closestPrediction == null)
                        {
                            DisableOverlay(DetectedPlayerOverlay!);

                            continue;
                        }

                        await AutoTrigger();

                        CalculateCoordinates(DetectedPlayerOverlay, closestPrediction, scaleX, scaleY);

                        HandleAim(closestPrediction);

                        totalTime += stopwatch.ElapsedMilliseconds;
                        iterationCount++;
                    }
                }
                await Task.Delay(1); // Add a small delay to avoid high GPU/CPU usage
            }
            stopwatch.Stop();
        }
        #endregion
        #region AI Loop Functions
        #region misc
        private static async Task AutoTrigger()
        {
            if (Dictionary.toggleState["Auto Trigger"] &&
                (InputBindingManager.IsHoldingBinding("Aim Keybind") ||
                 InputBindingManager.IsHoldingBinding("Second Aim Keybind") ||
                 Dictionary.toggleState["Constant AI Tracking"]))
            {
                await MouseManager.DoTriggerClick();
                if (!Dictionary.toggleState["Aim Assist"] && !Dictionary.toggleState["Show Detected Player"])
                {
                    return;
                }
            }
        }

        private static async void UpdateFOV()
        {
            if (Dictionary.dropdownState["Detection Area Type"] == "Closest to Mouse" && Dictionary.toggleState["FOV"])
            {
                var mousePosition = WinAPICaller.GetCursorPosition();
                if (Dictionary.FOVWindow != null) await Application.Current.Dispatcher.BeginInvoke(() => Dictionary.FOVWindow.FOVStrictEnclosure.Margin = new Thickness(Convert.ToInt16(mousePosition.X / WinAPICaller.scalingFactorX) - 320, Convert.ToInt16(mousePosition.Y / WinAPICaller.scalingFactorY) - 320, 0, 0));
            }
        }
        #endregion
        #region ESP
        private static void DisableOverlay(DetectedPlayerWindow DetectedPlayerOverlay)
        {
            if (Dictionary.toggleState["Show Detected Player"] && Dictionary.DetectedPlayerOverlay != null)
            {
                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    if (Dictionary.toggleState["Show AI Confidence"])
                    {
                        DetectedPlayerOverlay!.DetectedPlayerConfidence.Opacity = 0;
                    }

                    if (Dictionary.toggleState["Show Tracers"])
                    {
                        DetectedPlayerOverlay!.DetectedTracers.Opacity = 0;
                    }

                    DetectedPlayerOverlay!.DetectedPlayerFocus.Opacity = 0;
                }));
            }
        }

        private void UpdateOverlay(DetectedPlayerWindow detectedPlayerOverlay)
        {
            double scalingFactorX = WinAPICaller.scalingFactorX;
            double scalingFactorY = WinAPICaller.scalingFactorY;

            double centerX = LastDetectionBox.X / scalingFactorX + (LastDetectionBox.Width / 2.0);
            double centerY = LastDetectionBox.Y / scalingFactorY;
            double boxWidth = LastDetectionBox.Width;
            double boxHeight = LastDetectionBox.Height;

            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateConfidence(detectedPlayerOverlay, centerX, centerY);
                UpdateTracers(detectedPlayerOverlay, centerX, centerY, boxHeight);
                UpdateFocusBox(detectedPlayerOverlay, centerX, centerY, boxWidth, boxHeight);
            });
        }
        private void UpdateConfidence(DetectedPlayerWindow detectedPlayerOverlay, double centerX, double centerY)
        {
            if (Dictionary.toggleState["Show AI Confidence"])
            {
                detectedPlayerOverlay.DetectedPlayerConfidence.Opacity = 1;
                detectedPlayerOverlay.DetectedPlayerConfidence.Content = $"{Math.Round((AIConf * 100), 2)}%";

                double labelEstimatedHalfWidth = detectedPlayerOverlay.DetectedPlayerConfidence.ActualWidth / 2.0;
                detectedPlayerOverlay.DetectedPlayerConfidence.Margin = new Thickness(centerX - labelEstimatedHalfWidth, centerY - detectedPlayerOverlay.DetectedPlayerConfidence.ActualHeight - 2, 0, 0);
            }
            else
            {
                detectedPlayerOverlay.DetectedPlayerConfidence.Opacity = 0;
            }
        }

        private void UpdateTracers(DetectedPlayerWindow detectedPlayerOverlay, double centerX, double centerY, double boxHeight)
        {
            bool showTracers = Dictionary.toggleState["Show Tracers"];
            detectedPlayerOverlay.DetectedTracers.Opacity = showTracers ? 1 : 0;

            if (showTracers)
            {
                detectedPlayerOverlay.DetectedTracers.X2 = centerX;
                detectedPlayerOverlay.DetectedTracers.Y2 = centerY + boxHeight;
            }
        }

        private void UpdateFocusBox(DetectedPlayerWindow detectedPlayerOverlay, double centerX, double centerY, double boxWidth, double boxHeight)
        {
            detectedPlayerOverlay.DetectedPlayerFocus.Opacity = 1;
            detectedPlayerOverlay.DetectedPlayerFocus.Margin = new Thickness(centerX - (boxWidth / 2.0), centerY, 0, 0);
            detectedPlayerOverlay.DetectedPlayerFocus.Width = boxWidth;
            detectedPlayerOverlay.DetectedPlayerFocus.Height = boxHeight;

            detectedPlayerOverlay.Opacity = Dictionary.sliderSettings["Opacity"];
        }
        #endregion
        #region Coordinates
        private void CalculateCoordinates(DetectedPlayerWindow DetectedPlayerOverlay, Prediction closestPrediction, float scaleX, float scaleY)
        {
            AIConf = closestPrediction.Confidence;

            if (Dictionary.toggleState["Show Detected Player"] && Dictionary.DetectedPlayerOverlay != null)
            {
                UpdateOverlay(DetectedPlayerOverlay!);
                if (!Dictionary.toggleState["Aim Assist"]) return;
            }


            double YOffset = Dictionary.sliderSettings["Y Offset (Up/Down)"];
            double XOffset = Dictionary.sliderSettings["X Offset (Left/Right)"];

            double YOffsetPercentage = Dictionary.sliderSettings["Y Offset (%)"];
            double XOffsetPercentage = Dictionary.sliderSettings["X Offset (%)"];

            var rect = closestPrediction.Rectangle;
            double rectX = rect.X;
            double rectY = rect.Y;
            double rectWidth = rect.Width;
            double rectHeight = rect.Height;

            if (Dictionary.toggleState["X Axis Percentage Adjustment"])
            {
                detectedX = (int)((rectX + (rectWidth * (XOffsetPercentage / 100))) * scaleX);
            }
            else
            {
                detectedX = (int)((rectX + rectWidth / 2) * scaleX + XOffset);
            }

            if (Dictionary.toggleState["Y Axis Percentage Adjustment"])
            {
                detectedY = (int)((rectY + rectHeight - (rectHeight * (YOffsetPercentage / 100))) * scaleY + YOffset);
            }
            else
            {
                detectedY = CalculateDetectedY(scaleY, YOffset, closestPrediction);
            }
        }
        private static int CalculateDetectedY(float scaleY, double YOffset, Prediction closestPrediction)
        {
            var rect = closestPrediction.Rectangle;
            float yBase = rect.Y;
            float yAdjustment = Dictionary.dropdownState["Aiming Boundaries Alignment"] switch
            {
                "Center" => rect.Height / 2,
                "Bottom" => rect.Height,
                _ => 0 // Default case for "Top" and any other unexpected values
            };

            return (int)((yBase + yAdjustment) * scaleY + YOffset);
        }
        #endregion
        #region Mouse Movement
        private void HandleAim(Prediction closestPrediction)
        {
            if (Dictionary.toggleState["Aim Assist"] && (Dictionary.toggleState["Constant AI Tracking"]
                || Dictionary.toggleState["Aim Assist"] && InputBindingManager.IsHoldingBinding("Aim Keybind")
                || Dictionary.toggleState["Aim Assist"] && InputBindingManager.IsHoldingBinding("Second Aim Keybind")))
            {
                if (Dictionary.toggleState["Predictions"])
                {
                    HandlePredictions(kalmanPrediction, closestPrediction, detectedX, detectedY);
                }
                else
                {
                    MouseManager.MoveCrosshair(detectedX, detectedY);
                }
            }
        }

        private void HandlePredictions(KalmanPrediction kalmanPrediction, Prediction closestPrediction, int detectedX, int detectedY)
        {
            var predictionMethod = Dictionary.dropdownState["Prediction Method"];
            DateTime currentTime = DateTime.UtcNow;

            switch (predictionMethod)
            {
                case "Kalman Filter":
                    kalmanPrediction.UpdateKalmanFilter(new KalmanPrediction.Detection
                    {
                        X = detectedX,
                        Y = detectedY,
                        Timestamp = currentTime
                    });

                    var predictedPosition = kalmanPrediction.GetKalmanPosition();
                    MouseManager.MoveCrosshair(predictedPosition.X, predictedPosition.Y);
                    break;

                case "Shall0e's Prediction":
                    ShalloePredictionV2.xValues.Add(detectedX - PrevX);
                    ShalloePredictionV2.yValues.Add(detectedY - PrevY);

                    if (ShalloePredictionV2.xValues.Count > 5)
                    {
                        ShalloePredictionV2.xValues.RemoveAt(0);
                    }
                    if (ShalloePredictionV2.yValues.Count > 5)
                    {
                        ShalloePredictionV2.yValues.RemoveAt(0);
                    }

                    PrevX = detectedX;
                    PrevY = detectedY;

                    MouseManager.MoveCrosshair(ShalloePredictionV2.GetSPX(), detectedY);
                    break;

                case "wisethef0x's EMA Prediction":
                    wtfpredictionManager.UpdateDetection(new WiseTheFoxPrediction.WTFDetection
                    {
                        X = detectedX,
                        Y = detectedY,
                        Timestamp = currentTime
                    });

                    var wtfpredictedPosition = wtfpredictionManager.GetEstimatedPosition();
                    MouseManager.MoveCrosshair(wtfpredictedPosition.X, detectedY);
                    break;
            }
        }
        #endregion
        #region Prediction (AI Work)

        private async Task<Prediction?> GetClosestPrediction(bool useMousePosition = true)
        {
            var cursorPosition = WinAPICaller.GetCursorPosition();

            targetX = Dictionary.dropdownState["Detection Area Type"] == "Closest to Mouse" ? cursorPosition.X : ScreenWidth / 2;
            targetY = Dictionary.dropdownState["Detection Area Type"] == "Closest to Mouse" ? cursorPosition.Y : ScreenHeight / 2;

            Rectangle detectionBox = new(targetX - IMAGE_SIZE / 2, targetY - IMAGE_SIZE / 2, IMAGE_SIZE, IMAGE_SIZE);

            detectionBox = MathClass.ClampRectangle(detectionBox, ScreenWidth, ScreenHeight);

            Bitmap? frame = _captureManager.ScreenGrab(detectionBox);
            if (frame == null) return null;

            float[] inputArray = MathClass.BitmapToFloatArray(frame);
            if (inputArray == null) return null;

            Tensor<float> inputTensor = new DenseTensor<float>(inputArray, new int[] { 1, 3, frame.Height, frame.Width });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", inputTensor) };
            if (_modelManager._onnxModel == null) return null;

            using var results = _modelManager._onnxModel.Run(inputs, _modelManager._outputNames/*, _modeloptions*/);
            var outputTensor = results[0].AsTensor<float>();

            // Calculate the FOV boundaries
            float FovSize = (float)Dictionary.sliderSettings["FOV Size"];
            float fovMinX = (IMAGE_SIZE - FovSize) / 2.0f;
            float fovMaxX = (IMAGE_SIZE + FovSize) / 2.0f;
            float fovMinY = (IMAGE_SIZE - FovSize) / 2.0f;
            float fovMaxY = (IMAGE_SIZE + FovSize) / 2.0f;

            var (KDpoints, KDPredictions) = PrepareKDTreeData(outputTensor, detectionBox, fovMinX, fovMaxX, fovMinY, fovMaxY);

            if (KDpoints.Count == 0 || KDPredictions.Count == 0)
            {
                if (Dictionary.toggleState["Collect Data While Playing"] && !Dictionary.toggleState["Constant AI Tracking"] && !Dictionary.toggleState["Auto Label Data"])
                {
                    SaveFrame(frame);
                } // Save images if the user wants to even if theres nothing detected
                return null;
            }

            var tree = new KDTree<double, Prediction>(2, KDpoints.ToArray(), KDPredictions.ToArray(), MathClass.L2Norm_Squared_Double);
            var nearest = tree.NearestNeighbors(new double[] { IMAGE_SIZE / 2.0, IMAGE_SIZE / 2.0 }, 1);

            if (nearest != null && nearest.Length > 0)
            {
                // Translate coordinates
                var nearestPrediction = nearest[0].Item2;
                float translatedXMin = nearestPrediction.Rectangle.X + detectionBox.Left;
                float translatedYMin = nearestPrediction.Rectangle.Y + detectionBox.Top;
                LastDetectionBox = new RectangleF(translatedXMin, translatedYMin, nearestPrediction.Rectangle.Width, nearestPrediction.Rectangle.Height);

                CenterXTranslated = nearestPrediction.CenterXTranslated;
                CenterYTranslated = nearestPrediction.CenterYTranslated;

                SaveFrame(frame, nearestPrediction);

                return nearestPrediction;
            }
            return null;
        }

        private static (List<double[]>, List<Prediction>) PrepareKDTreeData(Tensor<float> outputTensor, Rectangle detectionBox, float fovMinX, float fovMaxX, float fovMinY, float fovMaxY)
        {
            float minConfidence = (float)Dictionary.sliderSettings["AI Minimum Confidence"] / 100.0f;

            var KDpoints = new List<double[]>(NUM_DETECTIONS);
            var KDpredictions = new List<Prediction>(NUM_DETECTIONS);

            for (int i = 0; i < NUM_DETECTIONS; i++)
            {
                float objectness = outputTensor[0, 4, i];
                if (objectness < minConfidence) continue;

                float x_center = outputTensor[0, 0, i];
                float y_center = outputTensor[0, 1, i];
                float width = outputTensor[0, 2, i];
                float height = outputTensor[0, 3, i];

                float x_min = x_center - width / 2;
                float y_min = y_center - height / 2;
                float x_max = x_center + width / 2;
                float y_max = y_center + height / 2;

                if (x_min < fovMinX || x_max > fovMaxX || y_min < fovMinY || y_max > fovMaxY) continue;

                RectangleF rect = new(x_min, y_min, width, height);
                Prediction prediction = new()
                {
                    Rectangle = rect,
                    Confidence = objectness,
                    CenterXTranslated = (x_center - detectionBox.Left) / IMAGE_SIZE,
                    CenterYTranslated = (y_center - detectionBox.Top) / IMAGE_SIZE
                };

                KDpoints.Add(new double[] { x_center, y_center });
                KDpredictions.Add(prediction);
            }

            KDpoints.TrimExcess();
            KDpredictions.TrimExcess();

            return (KDpoints, KDpredictions);
        }
        #endregion
        #endregion AI Loop Functions

        #region Screen Capture


        private void SaveFrame(Bitmap frame, Prediction? DoLabel = null)
        {
            if (!Dictionary.toggleState["Collect Data While Playing"]) return;
            if (Dictionary.toggleState["Constant AI Tracking"]) return;
            if ((DateTime.Now - lastSavedTime).TotalMilliseconds < 500) return;

            lastSavedTime = DateTime.Now;
            string uuid = Guid.NewGuid().ToString();

            string imagePath = Path.Combine("bin", "images", $"{uuid}.jpg");
            frame.Save(imagePath);
            if (Dictionary.toggleState["Auto Label Data"] && DoLabel != null)
            {
                var labelPath = Path.Combine("bin", "labels", $"{uuid}.txt");

                float x = (DoLabel!.Rectangle.X + DoLabel.Rectangle.Width / 2) / frame.Width;
                float y = (DoLabel!.Rectangle.Y + DoLabel.Rectangle.Height / 2) / frame.Height;
                float width = DoLabel.Rectangle.Width / frame.Width;
                float height = DoLabel.Rectangle.Height / frame.Height;

                File.WriteAllText(labelPath, $"0 {x} {y} {width} {height}");
            }
        }
        #endregion Screen Capture

        public void Dispose()
        {
            // Stop the loop
            _isAiLoopRunning = false;
            if (_aiLoopThread != null && _aiLoopThread.IsAlive)
            {
                if (!_aiLoopThread.Join(TimeSpan.FromSeconds(1)))
                {
                    _aiLoopThread.Interrupt(); // Force join the thread (may error..)
                }
            }

            DisposeResources();
        }

        private void DisposeResources()
        {
            if (Dictionary.dropdownState["Screen Capture Method"] == "DirectX")
            {
                _captureManager.DisposeD3D11();
            }
            else
            {
                _captureManager._captureBitmap?.Dispose();
            }

            _modelManager._onnxModel?.Dispose();
            _modelManager._modeloptions?.Dispose();
        }

        public class Prediction
        {
            public RectangleF Rectangle { get; set; }
            public float Confidence { get; set; }
            public float CenterXTranslated { get; set; }
            public float CenterYTranslated { get; set; }
        }
    }
}