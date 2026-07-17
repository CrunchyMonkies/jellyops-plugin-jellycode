/*
 * Distributed Transcoding — dashboard client script.
 *
 * Injected into index.html by the File Transformation plugin (see FileTransformationIntegration.cs)
 * and served from GET /DistributedTranscoding/ClientScript. It runs on every page but only acts on the
 * native transcoding settings page (/web/#/dashboard/playback/transcoding), where it:
 *   1. hides the native controls this plugin overrides (hardware accel, encoder preset, quality/CRF,
 *      thread count, tone mapping, throttling, segment cleanup) — those are managed per worker type; and
 *   2. adds a banner linking to the plugin's own settings page.
 *
 * The page is a compiled React (MUI) component with no per-control ids, so controls are matched by the
 * MUI `name` attribute, scoped under the page root #encodingSettingsPage. Hiding is done with an
 * injected <style> using :has() so it survives React re-renders (removing nodes would be re-added).
 */
(function () {
    'use strict';

    if (window.__distributedTranscodingDashboardPatch) {
        return;
    }
    window.__distributedTranscodingDashboardPatch = true;

    var STYLE_ID = 'dt-transcoding-hide-style';
    var BANNER_CLASS = 'dt-transcoding-managed-banner';
    var SETTINGS_URL = '#/configurationpage?name=DistributedTranscoding';

    // MUI `name` attributes of the native transcoding controls the mesh overrides. Kept in sync with
    // the plugin's "Transcoding defaults" settings tab, which exposes the same core encoding options.
    var HIDE_NAMES = [
        // Hardware acceleration + decoding + encoding
        'HardwareAccelerationType', 'VaapiDevice', 'QsvDevice',
        'h264', 'hevc', 'mpeg1video', 'mpeg2video', 'mpeg4', 'vc1', 'vp8', 'vp9', 'av1',
        'EnableDecodingColorDepth10Hevc', 'EnableDecodingColorDepth10Vp9',
        'EnableDecodingColorDepth10HevcRext', 'EnableDecodingColorDepth12HevcRext',
        'EnableEnhancedNvdecDecoder', 'PreferSystemNativeHwDecoder',
        'EnableHardwareEncoding', 'EnableIntelLowPowerH264HwEncoder', 'EnableIntelLowPowerHevcHwEncoder',
        // Tone mapping
        'EnableVppTonemapping', 'VppTonemappingBrightness', 'VppTonemappingContrast',
        'EnableVideoToolboxTonemapping', 'EnableTonemapping', 'TonemappingAlgorithm', 'TonemappingMode',
        'TonemappingRange', 'TonemappingDesat', 'TonemappingPeak', 'TonemappingParam',
        // Encoder / quality / threads
        'EncoderPreset', 'H265Crf', 'H264Crf', 'EncodingThreadCount',
        // Throttling / segment cleanup
        'EnableThrottling', 'ThrottleDelaySeconds', 'EnableSegmentDeletion', 'SegmentKeepSeconds'
    ];

    function injectStyle() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }
        var rules = HIDE_NAMES.map(function (name) {
            var n = name.replace(/["\\]/g, '');
            return '#encodingSettingsPage .MuiFormControl-root:has([name="' + n + '"]),'
                 + '#encodingSettingsPage .MuiFormControlLabel-root:has([name="' + n + '"])';
        }).join(',');
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = rules + '{display:none !important;}';
        (document.head || document.documentElement).appendChild(style);
    }

    function onTranscodingRoute() {
        return (window.location.hash || '').indexOf('/dashboard/playback/transcoding') !== -1;
    }

    function ensureBanner() {
        var page = document.getElementById('encodingSettingsPage');
        if (!page || page.querySelector('.' + BANNER_CLASS)) {
            return;
        }
        var host = page.querySelector('.content-primary') || page;
        var banner = document.createElement('div');
        banner.className = BANNER_CLASS;
        banner.setAttribute('style',
            'margin:1em 0;padding:0.85em 1em;border-radius:6px;'
            + 'border:1px solid rgba(127,127,127,0.4);border-left:4px solid #00a4dc;'
            + 'background:rgba(0,164,220,0.08);line-height:1.5;');
        banner.innerHTML =
            '<strong>Managed by Distributed Transcoding.</strong> '
            + 'Hardware acceleration, encoder preset, quality, tone mapping, thread count, FFmpeg '
            + 'throttling and segment cleanup are controlled per worker type by the Distributed '
            + 'Transcoding plugin, so those controls are hidden here. '
            + '<a class="button-link emby-button" href="' + SETTINGS_URL + '" '
            + 'style="color:#00a4dc;text-decoration:underline;">Open plugin settings</a>';
        host.insertBefore(banner, host.firstChild);
    }

    function apply() {
        injectStyle(); // harmless globally — rules only match elements on the transcoding page
        if (onTranscodingRoute()) {
            ensureBanner();
        }
    }

    var pending = false;
    function schedule() {
        if (pending) {
            return;
        }
        pending = true;
        window.setTimeout(function () {
            pending = false;
            apply();
        }, 50);
    }

    function start() {
        injectStyle();
        try {
            new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        } catch (e) { /* body not ready — DOMContentLoaded fallback below still applies once */ }
        window.addEventListener('hashchange', schedule);
        apply();
    }

    if (document.body) {
        start();
    } else {
        document.addEventListener('DOMContentLoaded', start);
    }
})();
