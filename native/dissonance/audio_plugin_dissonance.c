/*
 * AudioPluginDissonance for Android arm64: the entry points DissonanceVoip imports
 * (Dissonance.Audio.Capture.AudioPluginDissonanceNative), without WebRTC or RNNoise. Dissonance ships this library
 * for desktop only; without it the capture thread dies on the first frame and the microphone sends nothing.
 *
 * The preprocessor passes the audio through and detects speech by level: a 10 ms frame (480 samples at 48 kHz) is
 * speech when its RMS is above the sensitivity's threshold, and speech holds for HANGOVER_FRAMES after the last loud
 * frame so words are not cut between syllables. No noise suppression or echo cancellation: the AEC filter state is
 * "not running", as when the mixer has no echo filter.
 */
#include <math.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#define EXPORT __attribute__((visibility("default")))

enum { ERR_OK = 0, ERR_NULL_POINTER = -5, ERR_BAD_SAMPLE_RATE = -7 };
enum { FILTER_NOT_RUNNING = 0 };
enum { HANGOVER_FRAMES = 30 };

/* RMS thresholds in dBFS for VadSensitivityLevels Low, Medium, High, VeryHigh: more sensitive, quieter speech */
static const float vad_threshold_db[] = { -30.0f, -36.0f, -42.0f, -48.0f };

typedef struct {
    float threshold_db;
    int hangover;
    bool speech;
} preprocessor;

static int frame_length(int sample_rate)
{
    switch (sample_rate) {
    case 8000: case 16000: case 32000: case 48000: return sample_rate / 100;
    default: return 0;
    }
}

EXPORT void *Dissonance_CreatePreprocessor(int ns_level, int aec_level, bool aec_delay_agnostic, bool aec_extended,
                                           bool aec_refined, int aecm_routing_mode, bool aecm_comfort_noise)
{
    preprocessor *p = calloc(1, sizeof(preprocessor));
    if (p) p->threshold_db = vad_threshold_db[1];
    return p;
}

EXPORT void Dissonance_DestroyPreprocessor(void *handle) { free(handle); }

EXPORT void Dissonance_ConfigureNoiseSuppression(void *handle, int ns_level) {}

EXPORT void Dissonance_ConfigureVadSensitivity(void *handle, int level)
{
    preprocessor *p = handle;
    if (p && level >= 0 && level < 4) p->threshold_db = vad_threshold_db[level];
}

EXPORT void Dissonance_ConfigureAecSuppression(void *handle, int aec_level, int aecm_routing) {}

EXPORT bool Dissonance_GetVadSpeechState(void *handle)
{
    preprocessor *p = handle;
    return p && p->speech;
}

EXPORT int Dissonance_PreprocessCaptureFrame(void *handle, int sample_rate, const float *input, float *output,
                                             int stream_delay)
{
    preprocessor *p = handle;
    int n = frame_length(sample_rate);
    if (!p || !input || !output) return ERR_NULL_POINTER;
    if (n == 0) return ERR_BAD_SAMPLE_RATE;

    double sum = 0.0;
    for (int i = 0; i < n; i++) sum += (double)input[i] * input[i];
    float db = 10.0f * log10f((float)(sum / n) + 1e-12f);
    if (db > p->threshold_db) p->hangover = HANGOVER_FRAMES;
    else if (p->hangover > 0) p->hangover--;
    p->speech = p->hangover > 0;

    if (output != input) memcpy(output, input, sizeof(float) * n);
    return ERR_OK;
}

/* The desktop library hands the preprocessor to its mixer echo filter; there is none here */
EXPORT bool Dissonance_PreprocessorExchangeInstance(void *previous, void *replacement) { return true; }

EXPORT int Dissonance_GetFilterState(void) { return FILTER_NOT_RUNNING; }

EXPORT void Dissonance_GetAecMetrics(float *buffer, int length)
{
    if (buffer && length > 0) memset(buffer, 0, sizeof(float) * length);
}

EXPORT void Dissonance_SetAgcIsOutputMutedState(void *handle, bool muted) {}

/* RNNoise: the denoised frame is the input itself (Dissonance mixes it with the input by its wet mix) */
EXPORT void *Dissonance_CreateRnnoiseState(void) { return calloc(1, 1); }

EXPORT void Dissonance_DestroyRnnoiseState(void *state) { free(state); }

EXPORT bool Dissonance_RnnoiseProcessFrame(void *state, int count, int sample_rate, const float *input, float *output)
{
    if (!state || !input || !output || count < 0) return false;
    memcpy(output, input, sizeof(float) * count);
    return true;
}

EXPORT int Dissonance_RnnoiseGetGains(void *state, float *output, int length) { return 0; }

/* Unity loads every AudioPlugin* library as a native audio plugin: this one defines no mixer effects */
EXPORT int UnityGetAudioEffectDefinitions(void ***definitions)
{
    if (definitions) *definitions = NULL;
    return 0;
}
