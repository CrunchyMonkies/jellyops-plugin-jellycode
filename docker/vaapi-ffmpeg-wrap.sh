#!/bin/sh
# ffmpeg wrapper for the Intel worker in the distributed-transcoding mesh.
#
# The Jellyfin *server* builds the ffmpeg command line, but the worker's render node is not the
# server's: device plugins and multi-GPU hosts hand each pod whichever node they allocated
# (renderD128, renderD129, ...). So the wrapper finds the node this container can actually see,
# with the same scan as jellyops' server-side startup script, and:
#
#   1. rewrites every /dev/dri/renderD<N> in the arguments to that node — a server that has its
#      own GPU emits `-init_hw_device vaapi=va:/dev/dri/renderD<server's node>` for its VAAPI/QSV
#      pipeline, which fails on the worker ("Device creation failed") unless repointed;
#   2. when the args request a VAAPI encoder but set up no device (a GPU-less server omits
#      `-init_hw_device`, because Jellyfin only emits it after probing a local device), prepends
#      `-init_hw_device vaapi=va:<node> -filter_hw_device va` plus `-hwaccel vaapi`, so decode
#      runs on the GPU too (software fallback for codecs the card can't decode, e.g. MPEG-4/Xvid).
#
# Node selection: DT_RENDER_DEVICE pins it; otherwise the first renderD* whose PCI vendor is
# DT_RENDER_VENDOR (default 0x8086, Intel); otherwise the first renderD*. With no node at all the
# args pass through unchanged. `exec` preserves the PID so the worker's stop/kill signalling still
# targets ffmpeg directly.
set -eu

FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg
: "${LIBVA_DRIVER_NAME:=iHD}"
export LIBVA_DRIVER_NAME

dev="${DT_RENDER_DEVICE:-}"
if [ -z "$dev" ]; then
  vendor="${DT_RENDER_VENDOR:-0x8086}"
  for d in /dev/dri/renderD*; do
    [ -c "$d" ] || continue
    if [ "$(cat "/sys/class/drm/${d##*/}/device/vendor" 2>/dev/null || true)" = "$vendor" ]; then
      dev="$d"; break
    fi
  done
fi
if [ -z "$dev" ]; then
  for d in /dev/dri/renderD*; do
    if [ -c "$d" ]; then dev="$d"; break; fi
  done
fi

if [ -n "$dev" ]; then
  # Rewrite in place: `for a do` iterates over a snapshot of the original args while the loop
  # appends each (possibly rewritten) arg and shifts the original off the front.
  for a do
    case "$a" in
      */dev/dri/renderD*) a=$(printf '%s\n' "$a" | sed "s#/dev/dri/renderD[0-9][0-9]*#$dev#g") ;;
    esac
    set -- "$@" "$a"
    shift
  done

  args="$*"
  case "$args" in
    *_vaapi*)
      case "$args" in
        *-init_hw_device*|*-vaapi_device*|*-hwaccel_device*) : ;;  # device already set up
        *)
          exec "$FFMPEG" -init_hw_device "vaapi=va:$dev" -hwaccel vaapi -hwaccel_device va -filter_hw_device va "$@" ;;
      esac ;;
  esac
fi
exec "$FFMPEG" "$@"
