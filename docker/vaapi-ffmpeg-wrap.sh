#!/bin/sh
# ffmpeg wrapper for the Intel worker in the distributed-transcoding mesh.
#
# The Jellyfin *server* builds the ffmpeg command line, but when the server has no local
# render device (e.g. it runs on a node without an Intel GPU) it selects a *_vaapi encoder
# yet omits the `-init_hw_device` that sets the device up — because Jellyfin only emits that
# after probing a local device. The actual encode runs here on the worker, which *does* have
# the Xe render node, so we complete the VAAPI device setup ourselves.
#
# If the args request a VAAPI encoder and no hw device is already configured, prepend
# `-init_hw_device vaapi=va:<render node> -filter_hw_device va`; otherwise pass through
# unchanged (software jobs and already-complete commands are untouched). `exec` preserves the
# PID so the worker's stop/kill signalling still targets ffmpeg directly.
set -eu

FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg
: "${LIBVA_DRIVER_NAME:=iHD}"
export LIBVA_DRIVER_NAME

args="$*"
case "$args" in
  *_vaapi*)
    case "$args" in
      *-init_hw_device*|*-vaapi_device*|*-hwaccel_device*) : ;;  # device already set up
      *)
        DEV=$(ls /dev/dri/renderD* 2>/dev/null | head -n1)
        if [ -n "$DEV" ]; then
          exec "$FFMPEG" -init_hw_device "vaapi=va:$DEV" -filter_hw_device va "$@"
        fi ;;
    esac ;;
esac
exec "$FFMPEG" "$@"
