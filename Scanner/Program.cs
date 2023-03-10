using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        readonly List<IMyCameraBlock> scanCameras;
        readonly IMyTextPanel overlayLCD;
        readonly IMyCameraBlock viewCamera;
        readonly int loopsPerTickLimit = 10;
        readonly float scanRange = 3000;
        readonly float azimuthLimit = 3;
        readonly float elevationLimit = 3;
        readonly float angleStep = 0.7f;
        readonly List<ScanInfo> scanInfos;
        readonly DebugAPI Draw;
        readonly List<Vector2I> fixedPoints = new List<Vector2I>();
        readonly List<string> cameraRaycastClaim;

        public Program()
        {
            Draw = new DebugAPI(this);
            MyIni ini = new MyIni();

            SEUtils.Setup(this, UpdateFrequency.Update10, true, "Scanner");

            if (SEUtils.CurrentProgrammableBlock.CustomData == "")
            {
                string viewConfigSectionName = "View-Config";
                ini.AddSection(viewConfigSectionName);
                ini.Set(viewConfigSectionName, "Transparent-LCD-Overlay", "Transparent LCD View");
                ini.SetComment(viewConfigSectionName, "Transparent-LCD-Overlay", "The lcd that is located infront of your View-Cam");

                ini.Set(viewConfigSectionName, "View-Camera", "Camera View");
                ini.SetComment(viewConfigSectionName, "View-Camera", "The camera that is located behind your Transparent-LCD-Overlay");

                SEUtils.CurrentProgrammableBlock.CustomData = ini.ToString();

                throw new Exception("Please configure this script. See CustomData");
            }
            else
            {
                ini.TryParse(SEUtils.CurrentProgrammableBlock.CustomData);
            }

            cameraRaycastClaim = new List<string>();
            Echo(Vector3D.Forward.ToString());
            scanInfos = new List<ScanInfo>();

            scanCameras = new List<IMyCameraBlock>();
            GridTerminalSystem.GetBlocksOfType(scanCameras, SEUtils.IsInGrid);

            overlayLCD = GridTerminalSystem.GetBlockWithName("Transparent LCD View") as IMyTextPanel;
            viewCamera = GridTerminalSystem.GetBlockWithName("Camera View") as IMyCameraBlock;

            foreach (var item in scanCameras)
            {
                item.EnableRaycast = true;
            }
            scanCameras.Remove(viewCamera);

            overlayLCD.ContentType = ContentType.SCRIPT;

            SEUtils.StartCoroutine(UpdateOverlay());
            SEUtils.StartCoroutine(UpdateKnowObjects());
            SEUtils.StartCoroutine(ScanArea());

            Draw.RemoveAll();
            Vector3D test;
            Vector3D.CreateFromAzimuthAndElevation(0, 0, out test);
            Echo(test.ToString());

            Vector3D halfLCD = new Vector3D(2.5d / 2, 2.5d / 2, 0.0001d);
            BoundingBoxD localBB = new BoundingBoxD(halfLCD, -halfLCD);
            var matrix = overlayLCD.WorldMatrix;
            matrix.Translation += matrix.Forward * 2.5d / 2;
            MyOrientedBoundingBoxD obb = new MyOrientedBoundingBoxD(localBB, matrix);

            double azimuthLimitRad = MathHelperD.ToRadians(azimuthLimit);
            double elevationLimitRad = MathHelperD.ToRadians(elevationLimit);

            for (int a = 0; a < 2; a++)
            {
                for (int e = 0; e < 2; e++)
                {
                    Vector3D res;
                    Vector3D.CreateFromAzimuthAndElevation(azimuthLimitRad * (a == 0 ? 1 : -1), elevationLimitRad * (e == 0 ? 1 : -1), out res);
                    Vector3D worldDir = Vector3D.TransformNormal(res, viewCamera.WorldMatrix);
                    worldDir *= 10;
                    fixedPoints.Add((Vector2I)WorldDirToLcd(overlayLCD, viewCamera, worldDir, obb));
                }
            }
        }

        public void Save()
        {

        }

        public void Main(string argument, UpdateType updateSource)
        {
            if (SEUtils.RuntimeUpdate(argument, updateSource))
            {
                var res = updateSource & UpdateType.IGC;
            }
        }

        Vector2I? WorldDirToLcd(IMyTextPanel lcd, IMyCameraBlock cam, Vector3D worldDir, MyOrientedBoundingBoxD obb)
        {
            worldDir.Normalize();
            worldDir *= 5;
            LineD line = new LineD(cam.WorldMatrix.Translation, cam.WorldMatrix.Translation + worldDir);
            var res = obb.Intersects(ref line);

            if (res != null)
            {
                Vector3D referenceWorldPosition = viewCamera.WorldMatrix.Translation; // block.WorldMatrix.Translation is the same as block.GetPosition() btw
                worldDir /= 5;
                Vector3D worldDirection = (lcd.WorldMatrix.Translation + worldDir * (double)res) - referenceWorldPosition; // This is a vector starting at the reference block pointing at your desired position
                                                                                                                            // Convert worldDirection into a local direction
                Vector3D localCoordinates = Vector3D.TransformNormal(worldDirection, MatrixD.Transpose(viewCamera.WorldMatrix));

                Vector3D multiplier = new Vector3D(Vector3D.Forward.X == 0 ? 1 : 0, Vector3D.Forward.Y == 0 ? 1 : 0, Vector3D.Forward.Z == 0 ? 1 : 0);
                localCoordinates *= multiplier;
                Vector2D screenPos = new Vector2D(localCoordinates.X, localCoordinates.Y);
                screenPos += new Vector2D(2.5d / 2, 2.5d / 2);
                screenPos *= 204.8f;
                return new Vector2I((int)screenPos.X, 512 - (int)screenPos.Y);
            }
            return null;
        }

        double GetSecondsAgo(DateTime date)
        {
            return (DateTime.Now - date).TotalSeconds;
        }

        IEnumerator ScanArea()
        {
            yield return new WaitForConditionMet(() => 1 == 0, 1000, -1, () => {
                Echo("Test");
                return true;
            });
            float currentAzimuth = -azimuthLimit;
            float currentElevation = -elevationLimit;

            while (true)
            {
                cameraRaycastClaim.Add("scan");
                yield return new WaitForConditionMet(() => scanCameras.Any(x => x.CanScan(scanRange)) && cameraRaycastClaim.First() == "scan", -1, 10);
                cameraRaycastClaim.Remove("scan");

                var cam = scanCameras.First(x => x.CanScan(scanRange));
                var rc = cam.Raycast(scanRange, currentElevation, currentAzimuth);
                currentAzimuth += angleStep;

                if (currentAzimuth >= azimuthLimit)
                {
                    currentAzimuth = -azimuthLimit;
                    currentElevation += angleStep;
                    if (currentElevation >= elevationLimit)
                    {
                        currentElevation = -elevationLimit;
                    }
                }
                if (!rc.IsEmpty())
                {
                    if (!scanInfos.Any(x => x.DetectedEntity.EntityId == rc.EntityId))
                    {
                        scanInfos.Add(new ScanInfo()
                        {
                            DetectedEntity = rc,
                            TimeStamp = DateTime.Now
                        }
                        );
                    }
                }
                yield return new WaitForNextTick();
            }
        }

        IEnumerator UpdateKnowObjects()
        {
            while (true)
            {
                ScanInfo[] infos = scanInfos.ToArray();
                int currentLoops = 0;
                foreach (var item in infos)
                {
                    var position = item.PredictPosition();
                    cameraRaycastClaim.Add("update");
                    yield return new WaitForConditionMet(() =>
                    {
                        position = item.PredictPosition();
                        return scanCameras.Any(x => x.CanScan(position)) && cameraRaycastClaim.First() == "update";
                    }, -1, 300);
                    cameraRaycastClaim.Remove("update");
                    Draw.DrawLine(viewCamera.GetPosition(), position, Color.Red, 0.2f, 10, true);
                    var rc = scanCameras.First(x => x.CanScan(position)).Raycast(position);

                    if (!rc.IsEmpty())
                    {
                        if (scanInfos.Any(x => x.DetectedEntity.EntityId == rc.EntityId))
                        {
                            int infoIndex = scanInfos.IndexOf(item);
                            scanInfos[infoIndex].DetectedEntity = rc;
                            scanInfos[infoIndex].TimeStamp = DateTime.Now;
                        }
                        else
                        {
                            scanInfos.Add(new ScanInfo()
                                {
                                    DetectedEntity = rc,
                                    TimeStamp = DateTime.Now
                                }
                            );
                        }
                    }
                    else
                    {
                        if ((DateTime.Now - item.TimeStamp).TotalSeconds >= 15)
                        {
                            scanInfos.Remove(item);
                        }
                    }

                    currentLoops++;
                    if (currentLoops >= loopsPerTickLimit)
                    {
                        yield return new WaitForNextTick();
                        currentLoops = 0;
                    }
                }
                yield return new WaitForNextTick();
            }
        }

        IEnumerator UpdateOverlay()
        {
            while (true)
            {
                ScanInfo[] infos = scanInfos.ToArray();
                int currentLoops = 0;
                var df = overlayLCD.DrawFrame();

                foreach (var item in infos)
                {
                    Vector3D halfLCD = new Vector3D(2.5d / 2, 2.5d / 2, 0.0001d);
                    BoundingBoxD localBB = new BoundingBoxD(halfLCD, -halfLCD);
                    var matrix = overlayLCD.WorldMatrix;
                    matrix.Translation += matrix.Forward * 2.5d / 2;
                    MyOrientedBoundingBoxD obb = new MyOrientedBoundingBoxD(localBB, matrix);
                    Vector3D direction = item.PredictPosition() - viewCamera.WorldMatrix.Translation;
                    direction.Normalize();
                    direction *= 5;
                    LineD line = new LineD(viewCamera.WorldMatrix.Translation, viewCamera.WorldMatrix.Translation + direction);
                    var res = obb.Intersects(ref line);
                    var point = WorldDirToLcd(overlayLCD, viewCamera, item.DetectedEntity.Position - viewCamera.WorldMatrix.Translation, obb);

                    if (point != null)
                    {
                        Color color = Color.Gray;
                        if (GetSecondsAgo(item.TimeStamp) < 5)
                        {
                            switch (item.DetectedEntity.Relationship)
                            {
                                case MyRelationsBetweenPlayerAndBlock.NoOwnership:
                                    color = Color.BurlyWood;
                                    break;
                                case MyRelationsBetweenPlayerAndBlock.Owner:
                                    color = Color.ForestGreen;
                                    break;
                                case MyRelationsBetweenPlayerAndBlock.FactionShare:
                                    color = Color.DarkGreen;
                                    break;
                                case MyRelationsBetweenPlayerAndBlock.Neutral:
                                    color = Color.White;
                                    break;
                                case MyRelationsBetweenPlayerAndBlock.Enemies:
                                    color = Color.Red;
                                    break;
                                case MyRelationsBetweenPlayerAndBlock.Friends:
                                    color = Color.Blue;
                                    break;
                                default:
                                    break;
                            }
                        }

                        df.Add(
                            new MySprite()
                            {
                                Type = SpriteType.TEXTURE,
                                Data = "SquareSimple",
                                Position = point,
                                Size = new Vector2(2, 2),
                                Color = color
                            }
                        );
                    }

                    currentLoops++;
                    if (currentLoops >= loopsPerTickLimit)
                    {
                        yield return new WaitForNextTick();
                        currentLoops = 0;
                    }
                }
                foreach (var item in fixedPoints)
                {
                    df.Add(
                            new MySprite()
                            {
                                Type = SpriteType.TEXTURE,
                                Data = "SquareSimple",
                                Position = item,
                                Size = new Vector2(1, 1),
                                Color = Color.BlueViolet
                            }
                        );
                }
                df.Dispose();
                yield return new WaitForNextTick();
            }
        }


        public class ScanInfo
        {
            public DateTime TimeStamp;
            private MyDetectedEntityInfo _de;
            public MyDetectedEntityInfo DetectedEntity
            {

                get { return _de; }
                set
                {
                    lastVelocity = _de.Velocity;
                    _de = value;
                }
            }
            public Vector3D lastVelocity = new Vector3D();

            public Vector3D PredictPosition()
            {
                double elapsedSeconds = (DateTime.Now - TimeStamp).TotalSeconds;
                Vector3D velocity = new Vector3D(DetectedEntity.Velocity.X, DetectedEntity.Velocity.Y, DetectedEntity.Velocity.Z);
                Vector3D acceleration = velocity - (lastVelocity.Length() == 0 ? velocity : lastVelocity);
                // pPredict = p0 + v0 * t + (a * t^2) / 2
                return ((Vector3D)DetectedEntity.Position) + velocity * elapsedSeconds + acceleration * Math.Pow(elapsedSeconds, 2) / 2;
            }
        }

        public class DebugAPI
        {
            public readonly bool ModDetected;

            public void RemoveDraw() => _removeDraw?.Invoke(_pb);
            Action<IMyProgrammableBlock> _removeDraw;

            public void RemoveAll() => _removeAll?.Invoke(_pb);
            Action<IMyProgrammableBlock> _removeAll;

            public void Remove(int id) => _remove?.Invoke(_pb, id);
            Action<IMyProgrammableBlock, int> _remove;

            public int DrawPoint(Vector3D origin, Color color, float radius = 0.2f, float seconds = DefaultSeconds, bool? onTop = null) => _point?.Invoke(_pb, origin, color, radius, seconds, onTop ?? _defaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, Vector3D, Color, float, float, bool, int> _point;

            public int DrawLine(Vector3D start, Vector3D end, Color color, float thickness = DefaultThickness, float seconds = DefaultSeconds, bool? onTop = null) => _line?.Invoke(_pb, start, end, color, thickness, seconds, onTop ?? _defaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, Vector3D, Vector3D, Color, float, float, bool, int> _line;

            public int DrawAABB(BoundingBoxD bb, Color color, Style style = Style.Wireframe, float thickness = DefaultThickness, float seconds = DefaultSeconds, bool? onTop = null) => _aabb?.Invoke(_pb, bb, color, (int)style, thickness, seconds, onTop ?? _defaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, BoundingBoxD, Color, int, float, float, bool, int> _aabb;

            public int DrawOBB(MyOrientedBoundingBoxD obb, Color color, Style style = Style.Wireframe, float thickness = DefaultThickness, float seconds = DefaultSeconds, bool? onTop = null) => _obb?.Invoke(_pb, obb, color, (int)style, thickness, seconds, onTop ?? _defaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, MyOrientedBoundingBoxD, Color, int, float, float, bool, int> _obb;

            public int DrawSphere(BoundingSphereD sphere, Color color, Style style = Style.Wireframe, float thickness = DefaultThickness, int lineEveryDegrees = 15, float seconds = DefaultSeconds, bool? onTop = null) => _sphere?.Invoke(_pb, sphere, color, (int)style, thickness, lineEveryDegrees, seconds, onTop ?? _defaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, BoundingSphereD, Color, int, float, int, float, bool, int> _sphere;

            public int DrawMatrix(MatrixD matrix, float length = 1f, float thickness = DefaultThickness, float seconds = DefaultSeconds, bool? onTop = null) => _matrix?.Invoke(_pb, matrix, length, thickness, seconds, onTop ?? _defaultOnTop) ?? -1;
            Func<IMyProgrammableBlock, MatrixD, float, float, float, bool, int> _matrix;

            public int DrawGPS(string name, Vector3D origin, Color? color = null, float seconds = DefaultSeconds) => _gps?.Invoke(_pb, name, origin, color, seconds) ?? -1;
            Func<IMyProgrammableBlock, string, Vector3D, Color?, float, int> _gps;

            public int PrintHUD(string message, Font font = Font.Debug, float seconds = 2) => _printHUD?.Invoke(_pb, message, font.ToString(), seconds) ?? -1;
            Func<IMyProgrammableBlock, string, string, float, int> _printHUD;

            public void PrintChat(string message, string sender = null, Color? senderColor = null, Font font = Font.Debug) => _chat?.Invoke(_pb, message, sender, senderColor, font.ToString());
            Action<IMyProgrammableBlock, string, string, Color?, string> _chat;

            public void DeclareAdjustNumber(out int id, double initial, double step = 0.05, Input modifier = Input.Control, string label = null) => id = _adjustNumber?.Invoke(_pb, initial, step, modifier.ToString(), label) ?? -1;
            Func<IMyProgrammableBlock, double, double, string, string, int> _adjustNumber;

            public double GetAdjustNumber(int id, double noModDefault = 1) => _getAdjustNumber?.Invoke(_pb, id) ?? noModDefault;
            Func<IMyProgrammableBlock, int, double> _getAdjustNumber;

            public int GetTick() => _tick?.Invoke() ?? -1;
            Func<int> _tick;

            public enum Style { Solid, Wireframe, SolidAndWireframe }
            public enum Input { MouseLeftButton, MouseRightButton, MouseMiddleButton, MouseExtraButton1, MouseExtraButton2, LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt, Tab, Shift, Control, Alt, Space, PageUp, PageDown, End, Home, Insert, Delete, Left, Up, Right, Down, D0, D1, D2, D3, D4, D5, D6, D7, D8, D9, A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z, NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9, Multiply, Add, Separator, Subtract, Decimal, Divide, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12 }
            public enum Font { Debug, White, Red, Green, Blue, DarkBlue }

            const float DefaultThickness = 0.02f;
            const float DefaultSeconds = -1;

            IMyProgrammableBlock _pb;
            readonly bool _defaultOnTop;

            public DebugAPI(MyGridProgram program, bool drawOnTopDefault = false)
            {
                if (program == null)
                    throw new Exception("Pass `this` into the API, not null.");

                _defaultOnTop = drawOnTopDefault;
                _pb = program.Me;

                var methods = _pb.GetProperty("DebugAPI")?.As<IReadOnlyDictionary<string, Delegate>>()?.GetValue(_pb);
                if (methods != null)
                {
                    Assign(out _removeAll, methods["RemoveAll"]);
                    Assign(out _removeDraw, methods["RemoveDraw"]);
                    Assign(out _remove, methods["Remove"]);
                    Assign(out _point, methods["Point"]);
                    Assign(out _line, methods["Line"]);
                    Assign(out _aabb, methods["AABB"]);
                    Assign(out _obb, methods["OBB"]);
                    Assign(out _sphere, methods["Sphere"]);
                    Assign(out _matrix, methods["Matrix"]);
                    Assign(out _gps, methods["GPS"]);
                    Assign(out _printHUD, methods["HUDNotification"]);
                    Assign(out _chat, methods["Chat"]);
                    Assign(out _adjustNumber, methods["DeclareAdjustNumber"]);
                    Assign(out _getAdjustNumber, methods["GetAdjustNumber"]);
                    Assign(out _tick, methods["Tick"]);
                    RemoveAll();
                    ModDetected = true;
                }
            }

            void Assign<T>(out T field, object method) => field = (T)method;
        }
    }
}
