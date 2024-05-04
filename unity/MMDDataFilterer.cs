using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Warudo.Core;
using Warudo.Core.Attributes;
using Warudo.Core.Data;
using Warudo.Core.Scenes;
using Warudo.Core.Server;
using Warudo.Plugins.Core.Mixins;
using Warudo.Plugins.MMD.Assets;
using Debug = UnityEngine.Debug;

namespace Playground {
    [AssetType(Id = "9860bd2e-edd3-44d4-b63d-32d1f4bfd4d6", Title = "MMD 数据筛选器")]
    public class MMDDataFilterer : Asset {

        [Markdown(primary: true)]
        public string Status = "请输入 MMD 文件根路径，然后点击加载。";

        protected bool HideMainSection() => currentFolder == null;
        
        [Mixin]
        public PlaybackMixin Playback;

        [Section("当前 MMD 配置")]
        [SectionHiddenIf(nameof(HideMainSection))]
        
        [DataInput]
        [Label("MMD 名称")]
        [Description("默认为文件夹名称，请更改为歌曲本身的名称。")]
        public string DataName;

        [DataInput]
        [AutoComplete(nameof(CompleteMotionFile), true)]
        [Label("动作文件（日文：モーション）")]
        [Description("选择正确的动作文件。注：1. Warudo 的 MMD 播放暂不完善，如果角色的腿部动作不正确，是正常现象（即只要上半身正常，且角色臀部可以正常移动即可）。2. 部分 MMD 可能针对不同角色制作了不同的动作文件，如有 TDA 初音（ミク/Miku）可优先选择；否则选择自己认识的、身高相近 150-160cm 的角色；如果没有认识的，就选动作看起来最自然的即可。3. 如果 MMD 是多人 MMD（可以通过运镜看出来，如果经常有空白运镜就说明是双人/多人用 MMD），并且没有单人的动作和运镜文件，请跳过此 MMD。4. 如果所有动作都不知道在播什么东西的，可以跳过此 MMD。")]
        public string MotionFile;
        
        private async UniTask<AutoCompleteList> CompleteMotionFile() {
            return AutoCompleteList.Single(currentVmdFiles.Select(it => new AutoCompleteEntry { label = it, value = it }));
        }
        
        [DataInput]
        [AutoComplete(nameof(CompleteCameraFile), true)]
        [Label("运镜文件（日文：カメラ 或 カメラモーション）")]
        [Description("选择正确的运镜文件。如有多个运镜文件，选择最自然的即可。如果没有运镜文件，请跳过此 MMD。")]
        public string CameraFile;
        
        private async UniTask<AutoCompleteList> CompleteCameraFile() {
            return AutoCompleteList.Single(currentVmdFiles.Select(it => new AutoCompleteEntry { label = it, value = it }));
        }
        
        [DataInput]
        [AutoComplete(nameof(CompleteMusicFile), true)]
        [Label("音频文件（WAV 格式）")]
        [Description("选择正确的音频文件。如果没有音频文件，请从 B 站或者 YouTube 搜索相关的 MMD，下载对应的音频，并转换成 WAV 格式。")]
        public string MusicFile;
        
        private async UniTask<AutoCompleteList> CompleteMusicFile() {
            return AutoCompleteList.Single(currentMusicFiles.Select(it => new AutoCompleteEntry { label = it, value = it }));
        }

        [DataInput]
        [Label("音频偏移")]
        [FloatSlider(-1f, 1f, 0.01f)]
        [Description("请重点关注音频与动作/运镜的同步情况，并确保误差在 ±0.05 秒内！注意不是仅仅让运镜对上节拍，否则会有运镜和动作相差四拍，乍看拍子是对的，但实际上整首的运镜都有较大误差。如有不确定，请从 B 站或者 YouTube 搜索相关的 MMD 作为参考，以对准音频与动作/运镜。往左拖动提前角色动作；往右拖动延后角色动作，单位为秒。通常的值在 -0.2 到 0.2 之间，但偶尔可能有偏移非常大的 MMD（尤其是自己下载音频的情况），敬请留意。可以在文本框内输入滑条范围以外的数字（例如1.5）。")]
        public float MusicOffset = 0f;
        
        [Trigger]
        [Label("打开此 MMD 文件夹")]
        public void OpenFolder() {
            if (currentFolder != null) {
                Process.Start(currentFolder);
            }
        }

        [Trigger]
        [Label("完成配置此 MMD")]
        [Description("如果所有以上配置都已经完成，可以点击此按钮保存配置，并开始下一个 MMD 的配置。")]
        [DisabledIf(nameof(DisableApprove))]
        public void Approve() {
            File.WriteAllText(Path.Combine(currentFolder, ".filtered"), "");
            var toPath = FromPath + "_filtered";
            // Copy to output folder
            var outputFolder = Path.Combine(toPath, DataName);
            if (!Directory.Exists(outputFolder)) {
                Directory.CreateDirectory(outputFolder);
            }
            File.Copy(Path.Combine(currentFolder, MotionFile), Path.Combine(outputFolder, "motion.vmd"), true);
            File.Copy(Path.Combine(currentFolder, CameraFile), Path.Combine(outputFolder, "camera.vmd"), true);
            File.Copy(Path.Combine(currentFolder, MusicFile), Path.Combine(outputFolder, "music" + Path.GetExtension(MusicFile)), true);

            File.WriteAllText(Path.Combine(outputFolder, "metadata.json"), JsonConvert.SerializeObject(new MMDFilteredMetadata {
                music_offset = MusicOffset
            }));
            
            Context.Service.Toast(ToastSeverity.Success, "已保存配置。", "MMD 名称：" + DataName);
                
            Clear();
            DoRefresh();
        }

        protected bool DisableApprove() {
            return MotionFile == null || CameraFile == null || MusicFile == null || VMDPlayer == null || VMDPlayer.UnityVMDPlayer == null || VMDPlayer.UnityVMDPlayer.VMDReader is not { FrameCount: > 0 };
        }
        
        [Trigger]
        [Label("跳过此 MMD")]
        [Description("如果 MMD 文件夹中没有运镜文件，或者所有动作文件无法播放，或者 MMD 为多人 MMD，可以跳过此 MMD。")]
        public async void Skip() {
            if (!await Context.Service.PromptConfirmation("确定吗？", "确定要跳过此 MMD 吗？跳过后将不会再次显示。")) {
                return;
            }
            File.WriteAllText(Path.Combine(currentFolder, ".skipped"), "");
            Clear();
            DoRefresh();
        }

        public void Clear() {
            // Clear
            SetDataInput(nameof(MotionFile), null, true);
            SetDataInput(nameof(CameraFile), null, true);
            SetDataInput(nameof(MusicFile), null, true);
            SetDataInput(nameof(MusicOffset), 0f, true);
        }
        
        [Trigger]
        [Label("刷新")]
        [Description("如果 MMD 播放出现问题，或文件夹内放置了新的文件，可以尝试刷新。")]
        public void Refresh() {
            DoRefresh();
        }
        
        [Trigger]
        [Label("去 B 站搜索")]
        public void SearchBilibili() {
            if (DataName != null) {
                Process.Start("https://search.bilibili.com/all?keyword=" + Uri.EscapeDataString(DataName + " MMD"));
            }
        }
        
        [Trigger]
        [Label("去 YouTube 搜索")]
        [Description("大陆用户需要科学上网。")]
        public void SearchYoutube() {
            if (DataName != null) {
                Process.Start("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(DataName + " MMD"));
            }
        }

        [Trigger]
        [Label("下载 YouTube 音频")]
        [Description("需放置 youtube-dl.exe 在 C:/bin 目录下。大陆用户需要科学上网。")]
        public async void YoutubeDL() {
            var sd = await Context.Service.PromptStructuredDataInput<YoutubeDLStructuredData>("输入 YouTube 视频 URL");
            if (sd == null) return;
            // Run "youtube-dl --extract-audio --audio-format wav URL"
            // Working directory is the current folder
            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "C:/bin/youtube-dl.exe",
                    Arguments = $"--extract-audio --audio-format wav \"{sd.URL}\"",
                    WorkingDirectory = currentFolder,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            Context.Service.ShowProgress("正在下载音频...");
            process.WaitForExit();
            Context.Service.HideProgress();
            if (process.ExitCode != 0) {
                Context.Service.Toast(ToastSeverity.Error, "下载音频失败", "点击查看错误", error);
                return;
            }
            Context.Service.Toast(ToastSeverity.Success, "下载音频成功！", "");
            DoRefresh();
        }
        
        public class YoutubeDLStructuredData : StructuredData {
            [DataInput]
            [Label("URL")]
            public string URL;
        }
        
        [Section("系统设置")]
        
        [DataInput]
        [Label("MMD 文件根路径")]
        [Description("请输入 MMD 文件夹的根路径。例如路径为 C:/mmd，那么该文件夹下应有 C:/mmd/第一个MMD，C:/mmd/第二个MMD，C:/mmd/第三个MMD 等等。")]
        public string FromPath = "";

        [Trigger]
        [Label("加载")]
        public void Load() {
            DoRefresh();
        }
        
        [DataInput]
        [Label("自动选择文件")]
        [Description("关闭后不会在刷新时自动选择文件。")]
        public bool AutoSelectFiles = true;
        
        [DataInput]
        [Label("VMD 播放器")]
        [Description("默认已配置，无需理会此选项。")]
        public VMDPlayerAsset VMDPlayer;

        private string currentFolder;
        private int processedCount;
        private int selectedIndex;
        private int totalCount;
        private List<string> currentVmdFiles;
        private List<string> currentMusicFiles;
        private FileSystemWatcher watcher;
        
        protected override void OnCreate() {
            base.OnCreate();

            Watch(nameof(MotionFile), async () => {
                if (VMDPlayer == null) return;
                if (MotionFile != null) {
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.MotionVMDSource),
                        "mmdmotion://local/" + Path.Combine(currentFolder, MotionFile));
                    await UniTask.WaitUntil(() => VMDPlayer.UnityVMDPlayer != null 
                        && VMDPlayer.UnityVMDPlayer.IsReady
                        && VMDPlayer.UnityVMDPlayer.VMDReader != null);
                    var frameCount = VMDPlayer.UnityVMDPlayer.VMDReader.FrameCount;
                    if (frameCount <= 0) {
                        Context.Service.PromptMessage("错误", "手动或自动选中的动作文件无法播放，请尝试其他动作文件！");
                        Playback.Disable();
                    } else {
                        Playback.Enable((float)frameCount / VMDPlayer.FPS);
                        Context.Service.Toast(ToastSeverity.Success, "已加载动作文件。", "");
                    }
                    if (VMDPlayer.MusicPlayer.IsPlaying) {
                        VMDPlayer.Play();
                    }
                } else {
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.MotionVMDSource), null);
                    Playback.Disable();
                }
            });
            Watch(nameof(CameraFile), () => {
                if (VMDPlayer == null) return;
                if (CameraFile != null) {
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.CameraVMDSource),
                        "mmdmotion://local/" + Path.Combine(currentFolder, CameraFile));
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.EnableVMDCamera), true);
                } else {
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.CameraVMDSource), null);
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.EnableVMDCamera), false);
                }
            });
            Watch(nameof(MusicFile), () => {
                if (VMDPlayer == null) return;
                if (MusicFile != null) {
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.MusicSource),
                        "audio://local/" + Path.Combine(currentFolder, MusicFile));
                    if (VMDPlayer.IsPlaying) {
                        VMDPlayer.Play();
                    }
                } else {
                    VMDPlayer.SetDataInput(nameof(VMDPlayer.MusicSource), null);
                    VMDPlayer.MusicPlayer.Stop();
                }
            });
            Watch(nameof(MusicOffset), () => {
                if (VMDPlayer == null) return;
                VMDPlayer.SetDataInput(nameof(VMDPlayer.MusicOffset), MusicOffset);
            });

            Playback.SetUseVolume(false);
            Playback.SetSeekInRealtime(true);
            Playback.SetUseLoop(false);
            Playback.OnSeek = t => {
                VMDPlayer.SetDataInput(nameof(VMDPlayer.Seeker), (int) (VMDPlayer.FPS * t));
            };
            Playback.OnPlay = () => {
                VMDPlayer.Play();
            };
            Playback.OnPause = () => {
                VMDPlayer.Pause();
            };
            Playback.OnStop = () => {
                VMDPlayer.Stop();
            };
            
            SetActive(true);
        }

        protected override void OnDestroy() {
            base.OnDestroy();
            VMDPlayer?.Stop();
            VMDPlayer?.MusicPlayer.Stop();
        }

        private void DoRefresh() {
            watcher?.Dispose();
            if (VMDPlayer != null) {
                if (VMDPlayer.UnityVMDPlayer != null) VMDPlayer.Stop();
                VMDPlayer.MusicPlayer.Stop();
            }
            
            if (string.IsNullOrEmpty(FromPath) || !Directory.Exists(FromPath)) {
                Context.Service.PromptMessage("错误", "请输入有效的 MMD 文件根路径！");
                return;
            }
            
            Clear();
            
            // List all folders in FromPath
            var folders = Directory.GetDirectories(FromPath);
            // Unprocessed are that does not contain a .filtered or .skipped file
            var unprocessedFolders = folders.Where(it => !File.Exists(Path.Combine(it, ".filtered")) && !File.Exists(Path.Combine(it, ".skipped"))).ToList();
            processedCount = folders.Length - unprocessedFolders.Count;
            totalCount = folders.Length;
            
            currentFolder = unprocessedFolders.FirstOrDefault();
            if (currentFolder == null) {
                // If all folders have been filtered, we're done
                Status = $"已整理完成 {folders.Length} 个 MMD！";
                BroadcastDataInput(nameof(Status));
                return;
            }
            
            currentVmdFiles = Directory.GetFiles(currentFolder, "*.vmd", SearchOption.AllDirectories)
                .Select(it => Path.GetRelativePath(currentFolder, it)).ToList();
            
            // If there is only one VMD file, skip this
            if (currentVmdFiles.Count == 1) {
                File.WriteAllText(Path.Combine(currentFolder, ".skipped"), "");
                DoRefresh();
                return;
            }

            if (AutoSelectFiles) {
                var list = new List<string>(currentVmdFiles);
                // Try to find the camera VMD file by heuristics
                var cameraFile = list.FirstOrDefault(it => it.Contains("カメラ") || it.Contains("cam", StringComparison.InvariantCultureIgnoreCase));
                if (cameraFile != null) {
                    SetDataInput(nameof(CameraFile), cameraFile, true);
                    list.Remove(cameraFile);
                    // Pick the first motion file remaining
                    if (list.Count > 0) {
                        var motionFile = list.FirstOrDefault(it => it.Contains("モーション") || it.Contains("motion", StringComparison.InvariantCultureIgnoreCase));
                        if (motionFile != null) {
                            SetDataInput(nameof(MotionFile), motionFile, true);
                        } else {
                            SetDataInput(nameof(MotionFile), list[0], true);
                        }
                    }
                }
            }

            currentMusicFiles = Directory.GetFiles(currentFolder, "*.*", SearchOption.AllDirectories).Where(it => 
                it.EndsWith(".wav", StringComparison.InvariantCultureIgnoreCase))
                .Select(it => Path.GetRelativePath(currentFolder, it)).ToList();
            
            // Auto pick the music file if there's only one
            if (AutoSelectFiles && currentMusicFiles.Count == 1) {
                Debug.Log("Auto picked music file: " + currentMusicFiles[0]);
                SetDataInput(nameof(MusicFile), currentMusicFiles[0], true);
            }
            
            DataName = Path.GetFileName(currentFolder);
            BroadcastDataInput(nameof(DataName));
            
            // Watch the current folder and refresh when it changes
            watcher = new FileSystemWatcher(currentFolder);
            watcher.Changed += (sender, args) => {
                DoRefresh();
            };

            Status = $"当前第 {processedCount + 1}/{totalCount} 个文件夹：**" + Path.GetFileName(currentFolder) + "**\n\n"
                + "文件夹中的 VMD 数量：**" + currentVmdFiles.Count + "**\n\n"
                + "文件夹中的音乐文件数量：**" + currentMusicFiles.Count + "**\n\n"
                + "为保证最佳延迟，请佩戴耳机 🎧";
            BroadcastDataInput(nameof(Status));
        }

        public override void OnUpdate() {
            base.OnUpdate();
            if (VMDPlayer != null && VMDPlayer.UnityVMDPlayer != null && VMDPlayer.UnityVMDPlayer.IsReady && VMDPlayer.UnityVMDPlayer.VMDReader != null) {
                Playback.Update(VMDPlayer.IsPlaying, (float) VMDPlayer.CurrentFrame / VMDPlayer.FPS);
            } else {
                Playback.Update(false, 0f);
            }
        }
    }

    [Serializable]
    public class MMDFilteredMetadata {
        public float music_offset;
    }
}
