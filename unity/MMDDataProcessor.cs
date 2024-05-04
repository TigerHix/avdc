using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Serialization;
using UnityVMDReader;
using Warudo.Core;
using Warudo.Core.Attributes;
using Warudo.Core.Scenes;
using Warudo.Core.Server;
using Warudo.Core.Utils;
using Warudo.Plugins.Core.Assets;
using Warudo.Plugins.Core.Assets.Character;
using Warudo.Plugins.MMD.Assets;
using Object = UnityEngine.Object;

namespace Playground {
    [AssetType(Id = "31f9c794-b228-4a00-8be3-7c3c66d469f4", Title = "MMD Data Processor")]
    public class MMDDataProcessor : Asset {

        private const string FromPath = "C:\\research\\avdc\\mmd_filtered";
        private const string ToPath = "C:\\research\\avdc\\mmd_processed";

        [DataInput]
        public bool FirstOnly;

        [DataInput]
        [IntegerSlider(-1, 30)]
        public int ShowShotIndex = -1;

        [Markdown(primary: true)]
        public string Status;

        [Trigger]
        public void Refresh() {
            DoRefresh();
        }

        [Trigger]
        public void Process() {
            DoProcess();
        }

        [Section("References")]
        [DataInput]
        public VMDPlayerAsset VMDPlayer;

        public void DoRefresh() {
            // Scan how many folders are in from path
            var fromFolders = Directory.GetDirectories(FromPath, "*", SearchOption.AllDirectories)
                .Where(it => File.Exists(Path.Combine(it, "motion.vmd")))
                .ToList();
            Status = $"Found {fromFolders.Count} folders. Ready to process.";
            BroadcastDataInput(nameof(Status));
        }

        private static string CreateMD5(string input) {
            return string.Join("", MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(input)).Select(it => it.ToString("x2")));
        }

        public async void DoProcess() {
            try {
                var processedCameraFiles = Directory.GetFiles(ToPath, "camera.vmd", SearchOption.AllDirectories)
                    .Select(it => new FileInfo(it).Length)
                    .ToHashSet();

                var fromFolders = Directory.GetDirectories(FromPath, "*", SearchOption.AllDirectories)
                    .Where(it => File.Exists(Path.Combine(it, "motion.vmd")))
                    .ToList();

                for (var index = 0; index < fromFolders.Count; index++) {
                    if (FirstOnly && index > 0) {
                        break;
                    }
                    var fromFolder = fromFolders[index];
                    Context.Service.ShowProgress("Processing " + index + "/" + fromFolders.Count, (float)(index + 1) / fromFolders.Count);

                    var motionFile = Path.Combine(fromFolder, "motion.vmd");
                    if (!File.Exists(motionFile)) {
                        throw new Exception("Motion file not found in " + fromFolder);
                    }
                    var cameraFile = Path.Combine(fromFolder, "camera.vmd");
                    if (!File.Exists(cameraFile)) {
                        throw new Exception("Camera file not found in " + fromFolder);
                    }
                    var musicFile = Path.Combine(fromFolder, "music.wav");
                    if (!File.Exists(musicFile)) {
                        throw new Exception("Music file not found in " + fromFolder);
                    }
                    var metadataFile = Path.Combine(fromFolder, "metadata.json");
                    if (!File.Exists(metadataFile)) {
                        throw new Exception("Metadata file not found in " + fromFolder);
                    }

                    var fileSize = new FileInfo(cameraFile).Length;
                    if (processedCameraFiles.Contains(fileSize)) {
                        Debug.Log("Skipping " + fromFolder + " because it's already processed.");
                        continue;
                    }
                    processedCameraFiles.Add(fileSize);

                    VMDPlayer.SetDataInput(nameof(VMDPlayer.MusicSource), null);
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.MotionVMDSource), "mmdmotion://local/" + motionFile);
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.CameraVMDSource), "mmdmotion://local/" + cameraFile);

                    await UniTask.WaitUntil(() => VMDPlayer.UnityVMDPlayer != null
                        && VMDPlayer.UnityVMDPlayer.IsReady
                        && VMDPlayer.UnityVMDPlayer.VMDReader != null);

                    var vmd = VMDPlayer.CameraVMD;

                    if (vmd.CameraFrames.Count > vmd.CameraFrames.Last().Frame / 30f / 0.5f) {
                        Debug.Log("Too many camera frames. Skipping " + fromFolder);
                        continue;
                    }
                    if (vmd.CameraFrames.Count <= 10) {
                        Debug.Log("Too few camera frames. Skipping " + fromFolder);
                        continue;
                    }

                    var toFolder = Path.Combine(ToPath, Path.GetFileName(fromFolder));
                    while (Directory.Exists(toFolder)) {
                        toFolder += "_";
                    }
                    Directory.CreateDirectory(toFolder);

                    var shots = new List<CameraShot>();
                    var neck = VMDPlayer.UnityVMDPlayer.Animator.GetBoneTransform(HumanBodyBones.Neck);
                    var spine = VMDPlayer.UnityVMDPlayer.Animator.GetBoneTransform(HumanBodyBones.Spine);
                    for (var i = 0; i < vmd.CameraFrames.Count - 2; i++) {
                        var fromFrame = vmd.CameraFrames[i];
                        var toFrame = vmd.CameraFrames[i + 1];
                        if (toFrame.Frame - fromFrame.Frame == 1) {
                            // This is a jump cut. Skip it.
                            continue;
                        }

                        var shot = new CameraShot {
                            duration = (toFrame.Frame - fromFrame.Frame) / 30.0f,
                        };
                        VMDPlayer.SetDataInput(nameof(VMDPlayer.Seeker), fromFrame.Frame);
                        VMDPlayer.UnityVMDPlayer.Update();

                        shot.character_pos = neck.position;
                        shot.character_rot_y = spine.rotation.y;
                        var (pos, rot, dist, fov) = GetCameraInfo(VMDPlayer.UnityVMDPlayer, vmd, VMDPlayer.Character);
                        shot.camera_pos = pos;
                        shot.camera_rot = rot;
                        shot.camera_distance = dist;
                        shot.camera_fov = fov;

                        VMDPlayer.SetDataInput(nameof(VMDPlayer.Seeker), toFrame.Frame);
                        VMDPlayer.UnityVMDPlayer.Update();

                        shot.character_pos_offset = neck.position - shot.character_pos;
                        shot.character_rot_y_offset = spine.rotation.y - shot.character_rot_y;
                        (pos, rot, dist, fov) = GetCameraInfo(VMDPlayer.UnityVMDPlayer, vmd, VMDPlayer.Character);
                        shot.camera_pos_offset = pos - shot.camera_pos;
                        shot.camera_rot_offset = rot - shot.camera_rot;
                        shot.camera_distance_offset = dist - shot.camera_distance;
                        shot.camera_fov_offset = fov - shot.camera_fov;
                        
                        Debug.Log(shot.camera_distance_offset + "/" + shot.camera_fov_offset);

                        shots.Add(shot);
                    }

                    // Rotate
                    List<CameraShot> RotateByYAxis(List<CameraShot> shots, float degrees) {
                        var rot = Quaternion.Euler(0, degrees, 0);
                        var newShots = new List<CameraShot>();
                        foreach (var shot in shots) {
                            var newShot = new CameraShot {
                                duration = shot.duration,
                                character_pos = rot * shot.character_pos,
                                character_pos_offset = rot * shot.character_pos_offset,
                                character_rot_y = (shot.character_rot_y + degrees).WrappedEulerAngle(),
                                character_rot_y_offset = shot.character_rot_y_offset,
                                camera_pos = rot * shot.camera_pos,
                                camera_pos_offset = rot * shot.camera_pos_offset,
                                camera_rot = shot.camera_rot + new Vector3(0, degrees, 0),
                                camera_rot_offset = shot.camera_rot_offset,
                                camera_distance = shot.camera_distance,
                                camera_distance_offset = shot.camera_distance_offset,
                                camera_fov = shot.camera_fov,
                                camera_fov_offset = shot.camera_fov_offset,
                            };
                            newShots.Add(newShot);
                        }
                        return newShots;
                    }
                    // Flip
                    List<CameraShot> FlipByYAxis(List<CameraShot> shots) {
                        float FlipAngle(float angle) {
                            // Calculate the flipped angle
                            angle = 180 - angle;
                            // Normalize to ensure the angle remains within 0-360 range
                            return angle.WrappedEulerAngle();
                        }
                        var newShots = new List<CameraShot>();
                        foreach (var shot in shots) {
                            var newShot = new CameraShot {
                                duration = shot.duration,
                                character_pos = new Vector3(-shot.character_pos.x, shot.character_pos.y, shot.character_pos.z),
                                character_pos_offset = new Vector3(-shot.character_pos_offset.x, shot.character_pos_offset.y, shot.character_pos_offset.z),
                                character_rot_y = -shot.character_rot_y.WrappedEulerAngle(),
                                character_rot_y_offset = -shot.character_rot_y_offset,
                                camera_pos = new Vector3(-shot.camera_pos.x, shot.camera_pos.y, shot.camera_pos.z),
                                camera_pos_offset = new Vector3(-shot.camera_pos_offset.x, shot.camera_pos_offset.y, shot.camera_pos_offset.z),
                                camera_rot = new Vector3(shot.camera_rot.x, -shot.camera_rot.y.WrappedEulerAngle(), shot.camera_rot.z),
                                camera_rot_offset = new Vector3(shot.camera_rot_offset.x, -shot.camera_rot_offset.y.WrappedEulerAngle(), shot.camera_rot_offset.z),
                                camera_distance = shot.camera_distance,
                                camera_distance_offset = shot.camera_distance_offset,
                                camera_fov = shot.camera_fov,
                                camera_fov_offset = shot.camera_fov_offset
                            };
                            newShots.Add(newShot);
                        }
                        return newShots;
                    }
                    
                    var flippedShots = FlipByYAxis(shots);
                    for (var i = 0; i <= 35; i++) {
                        var rotatedShots = RotateByYAxis(shots, i * 10);
                        var rotatedFlipShots = RotateByYAxis(flippedShots, i * 10);
                        savedShots = rotatedShots;
                        await File.WriteAllTextAsync(Path.Combine(toFolder, "shots_" + i + ".json"), JsonConvert.SerializeObject(rotatedShots, Formatting.None));
                        await UniTask.DelayFrame(1);
                        savedShots = rotatedFlipShots;
                        await File.WriteAllTextAsync(Path.Combine(toFolder, "shots_flip_" + i + ".json"), JsonConvert.SerializeObject(rotatedFlipShots, Formatting.None));
                        await UniTask.DelayFrame(1);
                    }
                }
            } catch (Exception e) {
                Context.Service.Toast(ToastSeverity.Error, "Error", e.Message, e.StackTrace);
                return;
            } finally {
                Context.Service.HideProgress();
            }
            Context.Service.Toast(ToastSeverity.Success, "Success", "Processed " + Directory.GetDirectories(ToPath).Length + " folders.");
            if (!FirstOnly) savedShots = null;
        }


        private Transform cameraTargetRootTransform;
        private Transform cameraTargetTransform;
        private Transform cameraTransform;

        private List<CameraShot> savedShots;

        protected override void OnCreate() {
            base.OnCreate();
            DoRefresh();
            SetActive(true);
            cameraTargetRootTransform = new GameObject("VMD Camera Target Root").transform;
            cameraTargetTransform = new GameObject("VMD Camera Target").transform;
            cameraTargetTransform.SetParent(cameraTargetRootTransform);
            cameraTransform = new GameObject("VMD Camera").transform;
            cameraTransform.SetParent(cameraTargetTransform);
        }
        protected override void OnDestroy() {
            base.OnDestroy();
            if (cameraTargetRootTransform != null) {
                Object.Destroy(cameraTargetRootTransform.gameObject);
                cameraTargetRootTransform = null;
                cameraTargetTransform = null;
                cameraTransform = null;
            }
        }

        public override void OnUpdate() {
            base.OnUpdate();
            // Visualize in editor
            if (savedShots != null) {
                AVDCHelpers.VisualizeCameraShots(savedShots, ShowShotIndex);
            }
        }

        public (Vector3, Vector3, float, float) GetCameraInfo(UnityVMDPlayer UnityVMDPlayer, VMD CameraVMD, CharacterAsset Character) {
            const float MMDToUnityScale = 0.08f;

            var currentFrameInt = UnityVMDPlayer.FrameNumber;
            var currentRealFrame = UnityVMDPlayer.internalFrameNumber;
            var keyframeIndex = CameraVMD.CameraFrames.FindIndex(it => it.Frame > currentFrameInt) - 1;

            var keyframe = CameraVMD.CameraFrames[keyframeIndex];

            (Vector3, Quaternion, float) GetCameraTransform(Vector3 vmdPosition, Vector3 vmdEulerAngles, float distance) {
                var cameraLookAt = vmdPosition * MMDToUnityScale;
                var cameraRotation = vmdEulerAngles;
                cameraRotation.y = -cameraRotation.y;

                if (Character.IsNonNullAndActiveAndEnabled()) {
                    cameraTargetRootTransform.position = Character.Transform.Position;
                    cameraTargetRootTransform.rotation = Character.Transform.RotationQuaternion;
                } else {
                    cameraTargetRootTransform.position = Vector3.zero;
                    cameraTargetRootTransform.rotation = Quaternion.identity;
                }
                cameraTargetTransform.localPosition = cameraLookAt;
                cameraTargetTransform.localEulerAngles = cameraRotation;
                cameraTransform.localPosition = Vector3.zero; // new Vector3(0, 0, distance * MMDToUnityScale);
                //cameraTransform.localEulerAngles = new Vector3(0, 180, 0);

                return (cameraTransform.position, cameraTransform.rotation, distance * MMDToUnityScale);
            }

            // TODO: Orthographic

            float x;
            float y;
            float z;
            Vector3 rotation;
            float distance;
            float angle;
            if (keyframeIndex == CameraVMD.CameraFrames.Count - 1) {
                x = keyframe.Position.x;
                y = keyframe.Position.y;
                z = keyframe.Position.z;
                rotation = keyframe.Rotation;
                distance = keyframe.Distance;
                angle = keyframe.Angle;
            } else {
                var nextKeyframe = CameraVMD.CameraFrames[keyframeIndex + 1];
                if (nextKeyframe.Frame == keyframe.Frame + 1) {
                    // Do not interpolate
                    x = keyframe.Position.x;
                    y = keyframe.Position.y;
                    z = keyframe.Position.z;
                    rotation = keyframe.Rotation;
                    distance = keyframe.Distance;
                    angle = keyframe.Angle;
                } else {
                    var beginFrame = keyframeIndex == 0 ? 0 : keyframe.Frame;
                    var endFrame = nextKeyframe.Frame;

                    var xRate = keyframe.CameraInterpolation.GetInterpolationValue(CameraKeyFrame.Interpolation.BezierCurveNames.X,
                        currentRealFrame, beginFrame, endFrame);
                    var yRate = keyframe.CameraInterpolation.GetInterpolationValue(CameraKeyFrame.Interpolation.BezierCurveNames.Y,
                        currentRealFrame, beginFrame, endFrame);
                    var zRate = keyframe.CameraInterpolation.GetInterpolationValue(CameraKeyFrame.Interpolation.BezierCurveNames.Z,
                        currentRealFrame, beginFrame, endFrame);
                    var rotationRate = keyframe.CameraInterpolation.GetInterpolationValue(CameraKeyFrame.Interpolation.BezierCurveNames.Rotation,
                        currentRealFrame, beginFrame, endFrame);
                    var distanceRate = keyframe.CameraInterpolation.GetInterpolationValue(CameraKeyFrame.Interpolation.BezierCurveNames.Distance,
                        currentRealFrame, beginFrame, endFrame);
                    var angleRate = keyframe.CameraInterpolation.GetInterpolationValue(CameraKeyFrame.Interpolation.BezierCurveNames.Angle,
                        currentRealFrame, beginFrame, endFrame);

                    x = Mathf.Lerp(keyframe.Position.x, nextKeyframe.Position.x, xRate);
                    y = Mathf.Lerp(keyframe.Position.y, nextKeyframe.Position.y, yRate);
                    z = Mathf.Lerp(keyframe.Position.z, nextKeyframe.Position.z, zRate);
                    rotation = Vector3.Lerp(keyframe.Rotation, nextKeyframe.Rotation, rotationRate);
                    distance = Mathf.Lerp(keyframe.Distance, nextKeyframe.Distance, distanceRate);
                    angle = Mathf.Lerp(keyframe.Angle, nextKeyframe.Angle, angleRate);

                    // Debug.Log("keyframe: " + keyframe.Frame + " x: " + x + " y: " + y + " z: " + z + " rotation: " + rotation + " distance: " + distance + " angle: " + angle);
                }
            }

            var (pos, rot, distance2) = GetCameraTransform(new Vector3(x, y, z), rotation, distance);
            return (pos, rot.WrappedEulerAngles(), distance2, angle);
        }

    }

    [Serializable]
    public class MMDProcessedMetadata : MMDFilteredMetadata {
        public int fps;
    }

    [Serializable]
    public class CameraShot {
        // Model inputs
        public float duration;
        public Vector3 character_pos;
        public Vector3 character_pos_offset;
        public float character_rot_y;
        public float character_rot_y_offset;
        
        // Model outputs
        public Vector3 camera_pos;
        public Vector3 camera_pos_offset;
        public Vector3 camera_rot;
        public Vector3 camera_rot_offset;
        public float camera_distance;
        public float camera_distance_offset;
        public float camera_fov;
        public float camera_fov_offset;
    }

    public static class AVDCHelpers {
        
        public static void VisualizeCameraShots(IList<CameraShot> shots, int showIndex = -1, Color color = default) {
            if (color == default) {
                color = Color.blue;
            }
            for (var index = 0; index < shots.Count; index++) {
                if (showIndex >= 0 && index != showIndex) {
                    continue;
                }
                var shot = shots[index];
                Debug.DrawLine(shot.character_pos, shot.character_pos + shot.character_pos_offset, Color.green);
                var lastPoint = Vector3.negativeInfinity;
                var precision = 100f;
                for (var i = 0; i <= precision; i++) {
                    var t = i / precision;
                    var cp = shot.character_pos + shot.character_pos_offset * t;
                    var cr = shot.character_rot_y + shot.character_rot_y_offset * t;
                    var p = shot.camera_pos + shot.camera_pos_offset * t;
                    var r = shot.camera_rot + shot.camera_rot_offset * t;
                    var d = shot.camera_distance + shot.camera_distance_offset * t;
                    var (pos, rot) = GetCameraPositionAndRotation(cp, cr, p, r, d);
                    if (lastPoint != Vector3.negativeInfinity) {
                        Debug.DrawLine(lastPoint, pos, Color.Lerp(color, Color.black, t));
                    }
                    lastPoint = pos;
                }
            }
        }
        
        private static (Vector3, Vector3) GetCameraPositionAndRotation(Vector3 characterPos, float characterRotY, Vector3 cameraPos, Vector3 cameraRot, float distance)
        {
            // Create a quaternion for the character's rotation around the Y-axis
            Quaternion characterRotation = Quaternion.Euler(0f, characterRotY, 0f);
            
            // Adjust the camera rotation: Invert the Y axis rotation component and apply an additional 180 degrees to ensure it faces the character correctly
            Quaternion localCameraRotation = Quaternion.Euler(cameraRot.x, -cameraRot.y, cameraRot.z);

            // Calculate the final rotation by combining the character's rotation with the local camera rotation
            Quaternion combinedRotation = characterRotation * localCameraRotation * Quaternion.Euler(0, 180, 0);
            
            // Determine the final camera position:
            // Translate the camera's local position to the world position using the character's rotation
            Vector3 worldCameraPos = characterPos + characterRotation * cameraPos;
    
            // Calculate the camera's position as moving it backwards along its local Z-axis by 'distance'
            Vector3 cameraBackwards = combinedRotation * new Vector3(0, 0, -distance);
            
            Vector3 finalCameraPosition = worldCameraPos + cameraBackwards;
            
            // Convert the final quaternion rotation to Euler angles for the return value
            Vector3 finalCameraRotation = combinedRotation.eulerAngles;

            // Correct potential gimbal lock or representation issues by ensuring rotation is represented within 0-360 range
            finalCameraRotation.x = NormalizeAngle(finalCameraRotation.x);
            finalCameraRotation.y = NormalizeAngle(finalCameraRotation.y);
            finalCameraRotation.z = NormalizeAngle(finalCameraRotation.z);
            return (finalCameraPosition, finalCameraRotation);
        }
        
        static float NormalizeAngle(float angle)
        {
            while (angle > 360) angle -= 360;
            while (angle < 0) angle += 360;
            return angle;
        }
        
    }

}
