/* The fixed-argument wrappers Dissonance's managed code imports from its own opus build: opus_*_ctl are variadic,
 * which P/Invoke cannot call portably, so Dissonance ships these four next to the plain libopus API. */
#include <opus.h>

int dissonance_opus_encoder_ctl_in(OpusEncoder *st, int request, int value) { return opus_encoder_ctl(st, request, value); }
int dissonance_opus_encoder_ctl_out(OpusEncoder *st, int request, int *value) { return opus_encoder_ctl(st, request, value); }
int dissonance_opus_decoder_ctl_in(OpusDecoder *st, int request, int value) { return opus_decoder_ctl(st, request, value); }
int dissonance_opus_decoder_ctl_out(OpusDecoder *st, int request, int *value) { return opus_decoder_ctl(st, request, value); }
