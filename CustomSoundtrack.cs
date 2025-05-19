using BepInEx;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json;
using RoR2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine.SceneManagement;
using Path = System.IO.Path;

namespace CustomSoundtrack
{

    [BepInPlugin("com.penumbrah.customsoundtrack", "CustomSoundtrack", "2025.5.18")]

    public class CustomSoundtrack : BaseUnityPlugin
    {

        // settings
        private string playbackMode = "next";
        private string trackPath;

        // playlists
        private Dictionary<string, List<string>> bossPlaylists = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> postBossPlaylists = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> playlists = new Dictionary<string, List<string>>();

        // current game state
        private enum GameState
        {
            Normal,
            BossFight,
            PostBoss
        }
        private GameState currentState = GameState.Normal;
        private string currentScene = "";

        // current playback state
        private List<string> currentPlaylist = new List<string>();
        private string currentTrack = "";
        private bool paused = false;
        private bool start = false;

        private AudioFileReader currentAudioFileReader;
        private FadeInOutSampleProvider currentFadeInOutSampleProvider;
        private WaveOutEvent player = new WaveOutEvent();

        // logging
        private bool logging = false;
        private string logPath = "log.txt";

        // thread safety
        private bool locked = false;

        // called when the game is started
        public void Awake()
        {

            //// read settings from "settings.json"

            // load paths
            var pluginPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            logPath = Path.Combine(pluginPath, logPath);
            trackPath = pluginPath;

            // if "log.txt" doesn't exist, create it and enable logging
            if (!File.Exists(logPath))
                logging = true;

            // read "settings.json"
            Settings settings = null;
            try
            {
                settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(Path.Combine(pluginPath, "settings.json")));
            }
            catch (Exception error)
            {
                log("could not parse settings.json: " + Environment.NewLine + error);
            }

            // load settings
            if (settings != null)
            {

                // load track directory
                if (settings.trackPath != null && settings.trackPath != "")
                    trackPath = settings.trackPath;

                // load playback mode
                if (settings.playbackMode != null)
                    playbackMode = settings.playbackMode;

                // load playlists
                if (settings.playlists != null)
                    foreach (var playlist in settings.playlists)
                        if (playlist.ContainsKey("scenes"))
                            foreach (var scene in playlist["scenes"])
                            {

                                if (!playlists.Keys.Contains(scene))
                                    playlists.Add(scene, new List<string>());
                                if (playlist.ContainsKey("tracks"))
                                    foreach (var track in playlist["tracks"])
                                        playlists[scene].Add(Path.Combine(trackPath, track));

                                if (!bossPlaylists.Keys.Contains(scene))
                                    bossPlaylists.Add(scene, new List<string>());
                                if (playlist.ContainsKey("bossTracks"))
                                    foreach (var bossTrack in playlist["bossTracks"])
                                        bossPlaylists[scene].Add(Path.Combine(trackPath, bossTrack));

                                if (!postBossPlaylists.Keys.Contains(scene))
                                    postBossPlaylists.Add(scene, new List<string>());
                                if (playlist.ContainsKey("postBossTracks"))
                                    foreach (var tpTrack in playlist["postBossTracks"])
                                        postBossPlaylists[scene].Add(Path.Combine(trackPath, tpTrack));

                            }

            }

            // load all tracks in the track directory and subdirectories to the default playlist
            playlists.Add("_default", new List<string>());
            foreach (var track in Directory.GetFiles(trackPath, "*", SearchOption.AllDirectories)
                .Where(fileName => fileName.ToLower().EndsWith(".aiff") || fileName.ToLower().EndsWith(".mp3") || fileName.ToLower().EndsWith(".wav")))
                playlists["_default"].Add(track);

            if (logging)
            {

                log("track directory: " + trackPath);
                log("playback mode: " + playbackMode);

                log("playlists: ");
                foreach (var scene in playlists.Keys)
                {
                    log("    " + scene + ": ");
                    foreach (var track in playlists[scene])
                        log("        " + track);
                }

                log("boss playlists: ");
                foreach (var scene in bossPlaylists.Keys)
                {
                    log("    " + scene + ": ");
                    foreach (var track in bossPlaylists[scene])
                        log("        " + track);
                }

                log("tp playlists: ");
                foreach (var scene in postBossPlaylists.Keys)
                {
                    log("    " + scene + ": ");
                    foreach (var track in postBossPlaylists[scene])
                        log("        " + track);
                }

            }

            //// hooks

            // when a scene is loaded
            SceneManager.sceneLoaded += (scene, mode) => {

                if (scene.name == "splash")
                    start = true;

                if (start && currentScene != scene.name)
                {
                    currentState = GameState.Normal;
                    currentPlaylist = playlists["_default"];
                    currentScene = scene.name;
                    nextPlaylist();
                }

                if (logging)
                    log("loaded scene: " + scene.name);

            };


            // when tp event starts
            On.RoR2.TeleporterInteraction.RpcClientOnActivated += (orig, self, activator) => {

                orig(self, activator);

                if (currentState != GameState.BossFight && currentState != GameState.PostBoss)
                {
                    currentState = GameState.BossFight;
                    nextPlaylist();
                }

                if (logging)
                    log("activated teleporter");

            };

            // when tp event ends
            RoR2.TeleporterInteraction.onTeleporterChargedGlobal += (TeleporterInteraction teleporterInteraction) => {

                if (currentState != GameState.PostBoss)
                {
                    currentState = GameState.PostBoss;
                    nextPlaylist();
                }

                if (logging)
                    log("tp event over");

            };

            // when the mithrix fight starts
            On.EntityStates.Missions.BrotherEncounter.Phase1.OnEnter += (orig, self) => {

                orig(self);

                if (currentState != GameState.BossFight)
                {
                    currentState = GameState.BossFight;
                    nextPlaylist();
                }

                if (logging)
                    log("started mithrix fight");

            };

            // when the mithrix fight ends
            On.EntityStates.Missions.BrotherEncounter.BossDeath.OnEnter += (orig, self) => {

                orig(self);

                if (currentState != GameState.PostBoss)
                {
                    currentState = GameState.PostBoss;
                    nextPlaylist();
                }

                if (logging)
                    log("finished mithrix fight");

            };

            // when the false son fight starts
            On.EntityStates.MeridianEvent.Phase1.OnEnter += (orig, self) => {

                orig(self);

                if (currentState != GameState.BossFight)
                {
                    currentState = GameState.BossFight;
                    nextPlaylist();
                }

                if (logging)
                    log("started false son fight");

            };

            // when the false son fight ends
            On.EntityStates.MeridianEvent.Phase3.OnExit += (orig, self) => {

                orig(self);

                if (currentState != GameState.Normal)
                {
                    currentState = GameState.Normal;
                    nextPlaylist();
                }

                if (logging)
                    log("finished false son fight");

            };

            // when the game is focussed or unfocussed
            On.RoR2.AudioManager.AudioFocusedOnlyConVar.OnApplicationFocus += (orig, self, isFocussed) => {

                orig(self, isFocussed);

                if (isFocussed && player.PlaybackState == PlaybackState.Paused)
                {
                    paused = false;
                    if (!locked)
                        player.Play();
                }

                if (!isFocussed && player.PlaybackState == PlaybackState.Playing)
                {
                    paused = true;
                    if (!locked)
                        player.Pause();
                }

            };

            // when the game tries to play music
            On.RoR2.MusicController.PickCurrentTrack += disableOriginalSoundtrack;
            On.RoR2.MusicTrackOverride.PickMusicTrack += disableOriginalSoundtrack;

        }

        private void disableOriginalSoundtrack(On.RoR2.MusicTrackOverride.orig_PickMusicTrack orig, MusicController self, ref MusicTrackDef track)
        {
            orig(self, ref track);
            track = null;
        }

        private void disableOriginalSoundtrack(On.RoR2.MusicController.orig_PickCurrentTrack orig, MusicController self, ref MusicTrackDef track)
        {
            orig(self, ref track);
            track = null;
        }

        // called on every frame
        public void Update()
        {

            // play the next track if playback has stopped
            if (start && !locked && player.PlaybackState == PlaybackState.Stopped)
                nextTrack();

            applyVolume();

        }

        // select and start playing the next playlist
        private void nextPlaylist()
        {

            // try to find a playlist for the current stage
            // if no playlist is found, the default playlist is used
            switch (currentState)
            {
                case GameState.Normal:
                    if (playlists.ContainsKey(currentScene) && playlists[currentScene].Count > 0)
                        currentPlaylist = playlists[currentScene];
                    break;
                case GameState.BossFight:
                    if (bossPlaylists.ContainsKey(currentScene) && bossPlaylists[currentScene].Count > 0)
                        currentPlaylist = bossPlaylists[currentScene];
                    else
                        return;
                    break;
                case GameState.PostBoss:
                    if (postBossPlaylists.ContainsKey(currentScene) && postBossPlaylists[currentScene].Count > 0)
                        currentPlaylist = postBossPlaylists[currentScene];
                    break;
            }

            // if playbackMode is set to "next", leave the playlist unchanged and start playing the first track
            if (playbackMode == "next")
            {
                nextTrack();
                return;
            }

            // if playbackMode is set to "shuffle", randomize the playlist and start playing the first track
            var randomizer = new Random();
            currentPlaylist = currentPlaylist.OrderBy(_ => randomizer.Next()).ToList();
            if (playbackMode == "shuffle")
            {
                nextTrack();
                return;
            }

            // if playbackMode is set to "repeat", remove all but the first track from the playlist and start playing the track
            currentPlaylist = currentPlaylist.GetRange(0, 1);
            nextTrack();

        }

        // select and start the next track
        private void nextTrack()
        {

            if (currentPlaylist.Count > 0)
            {

                // select the first track from the current playlist
                var nextTrack = currentPlaylist[0];
                if (nextTrack != currentTrack || player.PlaybackState == PlaybackState.Stopped)
                {

                    var oldFadeInOutSampleProvider = currentFadeInOutSampleProvider;

                    // update current track
                    currentTrack = nextTrack;
                    currentAudioFileReader = new AudioFileReader(currentTrack);
                    currentFadeInOutSampleProvider = new FadeInOutSampleProvider(currentAudioFileReader);

                    // cycle playlist
                    currentPlaylist.Add(currentTrack);
                    currentPlaylist.RemoveAt(0);

                    applyVolume();

                    if (logging)
                    {

                        log("current track: " + currentTrack);

                        log("current playlist: ");
                        foreach (var track in currentPlaylist)
                            log("    " + track);

                    }

                    if (!locked)
                    {

                        locked = true;

                        var newTrack = currentFadeInOutSampleProvider;
                        var oldTrack = oldFadeInOutSampleProvider;

                        new Thread(() => {

                            // if still playing, fade out the current track and stop playing
                            if (player.PlaybackState != PlaybackState.Stopped)
                            {

                                oldTrack.BeginFadeOut(1500);
                                Thread.Sleep(1500);
                                player.Stop();

                            }

                            // load the next track and start playing
                            player.Init(newTrack);
                            if (!paused)
                                player.Play();

                            locked = false;

                        }).Start();

                    }

                }

            }

        }

        // read and apply the volume from the settings menu to the current track
        private void applyVolume()
        {
            if (currentAudioFileReader != null)
            {
                float.TryParse(AudioManager.cvVolumeMaster.GetString(), out float masterVolume);
                float.TryParse(AudioManager.cvVolumeMsx.GetString(), out float musicVolume);
                masterVolume /= 100f;
                musicVolume /= 100f;
                currentAudioFileReader.Volume = masterVolume * musicVolume;
            }
        }

        private void log(string message)
        {
            File.AppendAllText(logPath, "[" + DateTime.Now.ToString("hh:mm:ss") + "] " + message + Environment.NewLine);
        }

    }

    public class Settings
    {
        public string playbackMode { get; set; }
        public string trackPath { get; set; }
        public List<Dictionary<string, List<string>>> playlists { get; set; }
    }

}