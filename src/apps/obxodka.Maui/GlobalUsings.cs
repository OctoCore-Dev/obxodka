global using System.Buffers;
global using System.Collections.Concurrent;
global using System.Collections.ObjectModel;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.Net;
global using System.Net.NetworkInformation;
global using System.Net.Sockets;
global using System.Runtime.InteropServices;
global using System.Runtime.Versioning;
global using System.Security.Cryptography.X509Certificates;
global using System.Text.Json;
global using System.Threading.Channels;
global using CommunityToolkit.Maui;
global using CommunityToolkit.Maui.Views;
global using MauiIcons.Core;
global using MauiIcons.Fluent;
global using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;
global using RoundRectangleGeometry = Microsoft.Maui.Controls.Shapes.RoundRectangleGeometry;
global using Microsoft.Maui.LifecycleEvents;
global using obxodka.Client.Localization;
global using obxodka.Client.Models;
global using obxodka.Client.Platforms;
global using obxodka.Client.Telemetry;
global using obxodka.Config;
global using obxodka.Core;
global using obxodka.Core.Pipeline;
global using obxodka.Core.Transports;
global using obxodka.Controls.Dialogs;
global using obxodka.Controls.Ui;
global using obxodka.Helpers;
global using obxodka.Maui.Services;
global using obxodka.Maui.Theming.Models;
global using obxodka.Maui.Theming.Services;
global using obxodka.Models;
global using obxodka.Pages;
global using obxodka.Services;
global using obxodka.Shared.Logging;
global using obxodka.Shared.MtuEngine;
global using obxodka.Shared.Themes;
global using Plugin.InAppBilling;
global using SkiaSharp;
global using SkiaSharp.Views.Maui;
global using SkiaSharp.Views.Maui.Controls;
global using SkiaSharp.Views.Maui.Controls.Hosting;
global using obxodka.Pages.Account;
global using obxodka.Core.Models;
global using obxodka.Pages.Navigation;
global using obxodka.Pages.Settings;
global using obxodka.Pages.Store;
global using obxodka.Pages.Vpn;

#if WINDOWS
global using obxodka.Platforms.Windows;
#endif

[assembly: SupportedOSPlatform("android30.0")]
[assembly: SupportedOSPlatform("ios16.1")]
[assembly: SupportedOSPlatform("maccatalyst16.1")]
[assembly: SupportedOSPlatform("windows10.0.19041.0")]
