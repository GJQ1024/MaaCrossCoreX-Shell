using CommunityToolkit.Mvvm.Input;
using Org.BouncyCastle.Ocsp;
using System;
using System.Diagnostics;
using System.Windows.Input;

namespace MFAAvalonia.Helper;

public class MFAUrls
{
    public const string GitHub = "https://github.com/GJQ1024/MaaCrossCoreX";

    public const string GitHubIssues = $"{GitHub}/issues";
    
    public const string GitHubToken = $"https://github.com/settings/tokens";
    
    // 本仓没有上游的 issue 模板，直接走空白新建页。
    public const string NewIssueUri = $"{GitHubIssues}/new";

    public const string PurchaseLink = "https://mirrorchyan.com?rid=MFAAvalonia&source=mfaa-software";
}
