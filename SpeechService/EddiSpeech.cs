﻿using EddiDataDefinitions;

namespace EddiSpeechService
{
    public class EddiSpeech
    {
        public string message { get; private set; }
        public Ship ship { get; private set; }
        public int priority { get; private set; }
        public int volume { get; private set; }
        public string voice { get; private set; }
        public bool radio { get; private set; }
        public string eventType { get; private set; }

        // Calculated SpeechFX data
        public int echoDelay { get; set; }
        public int chorusLevel { get; set; }
        public int reverbLevel { get; set; }
        public int distortionLevel { get; set; }
        public int compressionLevel { get; set; }

        public EddiSpeech(string message, Ship ship = null, int priority = 3, string voice = null, bool radio = false, string eventType = null, int volume = 0)
        {
            this.message = message;
            this.ship = ship;
            this.priority = priority;
            this.volume = volume;
            this.voice = voice;
            this.radio = radio;
            this.eventType = eventType;

            EddiSpeech speech = SpeechService.Instance.GetSpeechFX(this);
            this.echoDelay = speech.echoDelay;
            this.chorusLevel = speech.chorusLevel;
            this.reverbLevel = speech.reverbLevel;
            this.distortionLevel = speech.distortionLevel;
            this.compressionLevel = speech.compressionLevel;
        }
    }
}
