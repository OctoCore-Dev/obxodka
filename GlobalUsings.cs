global using System.Buffers;
global using System.Diagnostics;
global using System.Globalization;
global using System.Net;
global using System.Net.Sockets;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Serialization;

#if OBXODKA_CLIENT
global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Collections.Frozen;
global using System.ComponentModel;
global using System.Diagnostics.CodeAnalysis;
global using System.Net.Http.Headers;
global using System.Net.Http.Json;
global using System.Net.Security;
global using System.Net.WebSockets;
global using System.Runtime.CompilerServices;
global using System.Security;
global using System.Security.Authentication;
global using System.Security.Cryptography.X509Certificates;
global using System.Text.Json.Serialization.Metadata;
global using System.Text.RegularExpressions;
global using System.Threading.Channels;
global using obxodka.Client.Localization;
global using obxodka.Client.Models;
global using obxodka.Client.Platforms;
global using obxodka.Client.Security;
global using obxodka.Client.Telemetry;
global using obxodka.Config;
global using obxodka.Core;
global using obxodka.Core.Pipeline;
global using obxodka.Core.Transports;
global using obxodka.Helpers;
global using obxodka.Models;
global using obxodka.Models.Responses;
global using obxodka.Services;
global using obxodka.Shared.Logging;
global using obxodka.Shared.MtuEngine;
global using obxodka.Shared.Stealth;
global using obxodka.Shared.Stealth.ClientHello;
#endif

#if OBXODKA_PLATFORM_LINUX
global using System.Collections.Concurrent;
global using System.Runtime.InteropServices;
global using System.Runtime.Versioning;
global using obxodka.Client.Platforms;
global using obxodka.Config;
global using obxodka.Core;
global using obxodka.Core.Pipeline;
global using obxodka.Core.Transports;
global using obxodka.Services;
global using obxodka.Shared.Logging;
global using obxodka.Shared.MtuEngine;
#endif

#if OBXODKA_PLATFORM_WINDOWS
global using System.Collections.Concurrent;
global using System.Diagnostics.CodeAnalysis;
global using System.Net.NetworkInformation;
global using System.Runtime.InteropServices;
global using System.Runtime.Versioning;
global using obxodka.Client.Localization;
global using obxodka.Client.Models;
global using obxodka.Client.Platforms;
global using obxodka.Config;
global using obxodka.Core;
global using obxodka.Core.Models;
global using obxodka.Core.Pipeline;
global using obxodka.Core.Transports;
global using obxodka.Helpers;
global using obxodka.Models;
global using obxodka.Services;
global using obxodka.Shared.Logging;
global using obxodka.Shared.MtuEngine;
#endif

#if OBXODKA_CLI
global using System.Net.NetworkInformation;
global using System.Runtime.InteropServices;
global using obxodka.Client.Models;
global using obxodka.Client.Platforms;
global using obxodka.Config;
global using obxodka.Core;
global using obxodka.Core.Models;
global using obxodka.Models;
global using obxodka.Models.Responses;
global using obxodka.Platforms.Linux;
global using obxodka.Platforms.Windows;
global using obxodka.Services;
global using obxodka.Shared.Logging;
global using obxodka.Shared.MtuEngine;
#endif

#if OBXODKA_AVALONIA
global using System.Collections.ObjectModel;
global using System.Net.NetworkInformation;
global using System.Runtime.InteropServices;
global using Avalonia;
global using Avalonia.Controls;
global using Avalonia.Controls.ApplicationLifetimes;
global using Avalonia.Media;
global using Avalonia.Threading;
global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;
global using obxodka.Client.Models;
global using obxodka.Client.Platforms;
global using obxodka.Config;
global using obxodka.Core;
global using obxodka.Core.Models;
global using obxodka.Models;
global using obxodka.Models.Responses;
global using obxodka.Platforms.Linux;
global using obxodka.Platforms.Windows;
global using obxodka.Services;
global using obxodka.Shared.Logging;
global using obxodka.Shared.MtuEngine;
#endif

#if OBXODKA_MAUI && (ANDROID || WINDOWS || IOS || MACCATALYST)
global using System.Collections.Concurrent;
global using System.Collections.ObjectModel;
global using System.Diagnostics.CodeAnalysis;
global using System.Net.NetworkInformation;
global using System.Runtime.InteropServices;
global using System.Runtime.Versioning;
global using System.Security.Cryptography.X509Certificates;
global using System.Threading.Channels;
global using CommunityToolkit.Maui;
global using CommunityToolkit.Maui.Views;
global using MauiIcons.Core;
global using MauiIcons.Fluent;
global using Microsoft.Maui.LifecycleEvents;
global using obxodka.Client.Localization;
global using obxodka.Client.Models;
global using obxodka.Client.Platforms;
global using obxodka.Client.Telemetry;
global using obxodka.Config;
global using obxodka.Controls.Dialogs;
global using obxodka.Controls.Ui;
global using obxodka.Core;
global using obxodka.Core.Models;
global using obxodka.Core.Pipeline;
global using obxodka.Core.Transports;
global using obxodka.Helpers;
global using obxodka.Maui.Services;
global using obxodka.Maui.Theming.Models;
global using obxodka.Maui.Theming.Services;
global using obxodka.Models;
global using obxodka.Pages;
global using obxodka.Pages.Account;
global using obxodka.Pages.Navigation;
global using obxodka.Pages.Settings;
global using obxodka.Pages.Store;
global using obxodka.Pages.Vpn;
global using obxodka.Services;
global using obxodka.Shared.Logging;
global using obxodka.Shared.MtuEngine;
global using obxodka.Shared.Themes;
global using Plugin.InAppBilling;
global using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;
global using RoundRectangleGeometry = Microsoft.Maui.Controls.Shapes.RoundRectangleGeometry;
global using SkiaSharp;
global using SkiaSharp.Views.Maui;
global using SkiaSharp.Views.Maui.Controls;
global using SkiaSharp.Views.Maui.Controls.Hosting;
#if WINDOWS
global using obxodka.Platforms.Windows;

[assembly: SuppressMessage(
    "Security",
    "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
    Scope = "member",
    Target = "~M:Microsoft.Windows.Foundation.UndockedRegFreeWinRTCS.NativeMethods.WindowsAppRuntime_EnsureIsLoaded~System.Int32",
    Justification = "Автоподключаемый системный файл из NuGet-пакета Microsoft.WindowsAppSDK.Foundation")]
#endif

[assembly: SupportedOSPlatform("android30.0")]
[assembly: SupportedOSPlatform("ios16.1")]
[assembly: SupportedOSPlatform("maccatalyst16.1")]
[assembly: SupportedOSPlatform("windows10.0.19041.0")]
#endif

#if OBXODKA_BENCHMARKS
global using System.Buffers.Binary;
global using System.Collections.Frozen;
global using BenchmarkDotNet.Attributes;
global using BenchmarkDotNet.Running;
global using obxodka.Core;
global using obxodka.Models;
global using obxodka.Shared.Stealth;
#endif

#if OBXODKA_CLIENT_TESTS
global using System.Buffers.Binary;
global using System.Diagnostics.CodeAnalysis;
global using System.IO.Compression;
global using System.Net.Http.Headers;
global using System.Net.NetworkInformation;
global using System.Net.Security;
global using System.Runtime.CompilerServices;
global using System.Security;
global using System.Security.Authentication;
global using System.Security.Cryptography.X509Certificates;
global using System.Threading.Channels;
global using System.Xml.Linq;
global using Moq;
global using Moq.Protected;
global using obxodka.Client.Diagnostics;
global using obxodka.Client.Localization;
global using obxodka.Client.Platforms;
global using obxodka.Client.Security;
global using obxodka.Client.Telemetry;
global using obxodka.Config;
global using obxodka.Core;
global using obxodka.Core.Models;
global using obxodka.Core.Transports;
global using obxodka.Helpers;
global using obxodka.Models;
global using obxodka.Services;
global using obxodka.Shared.Logging;
global using obxodka.Shared.MtuEngine;
global using obxodka.Shared.Stealth;
global using obxodka.Shared.Stealth.ClientHello;
global using obxodka.Shared.Themes;
global using OpenQA.Selenium.Appium;
global using OpenQA.Selenium.Appium.Windows;
#endif
