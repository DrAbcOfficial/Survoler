using System;
using Android.Runtime;
using Android.Webkit;
using Avalonia.Controls;
using Avalonia.Platform;
using Survoler.Rendering;

namespace Survoler.Android;

public sealed class AndroidWebViewPlatformPolicy : IWebViewPlatformPolicy
{
    public void ConfigureEnvironment(WebViewEnvironmentRequestedEventArgs args)
    {
        args.EnableDevTools = false;

        if (args is AndroidWebViewEnvironmentRequestedEventArgs androidArgs)
        {
            androidArgs.BuiltInZoomControls = true;
            androidArgs.DomStorageEnabled = false;
            androidArgs.DatabaseEnabled = false;
            androidArgs.DisableCache = true;
            androidArgs.DataDirectorySuffix = null;
        }
    }

    public void ConfigureAdapter(WebViewAdapterEventArgs args)
    {
        if (args.TryGetPlatformHandle() is not IAndroidWebViewPlatformHandle handle)
        {
            return;
        }

        WebView? webView = Java.Lang.Object.GetObject<WebView>(
            handle.WebKitWebView,
            JniHandleOwnership.DoNotTransfer);
        if (webView is null)
        {
            return;
        }

        webView.RemoveJavascriptInterface("postAvWebViewMessage");

        WebSettings settings = webView.Settings;
        settings.JavaScriptEnabled = false;
        settings.DomStorageEnabled = false;
        settings.DatabaseEnabled = false;
        settings.CacheMode = CacheModes.NoCache;
        settings.BlockNetworkLoads = true;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = false;
        settings.JavaScriptCanOpenWindowsAutomatically = false;
        settings.SetSupportMultipleWindows(false);
        settings.SetGeolocationEnabled(false);
        settings.SaveFormData = false;
        settings.MediaPlaybackRequiresUserGesture = true;
        settings.MixedContentMode = MixedContentHandling.NeverAllow;

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            settings.SafeBrowsingEnabled = false;
        }

        CookieManager.Instance?.SetAcceptThirdPartyCookies(webView, false);
    }
}
