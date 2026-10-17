#Requires -Version 5.1
# The manifest of a distribution APK as aapt2 dumps it (dump xmltree --file AndroidManifest.xml), read into a tree
# and checked against the distribution's guarantees (DESIGN 3.5, D-239). Functions only: Inspect-DistributionApk.ps1
# dot-sources this file, and so does the test runner over synthetic dumps. Every check reads the element the
# attribute belongs to: the same string elsewhere in the manifest never makes a check hold.

function ConvertFrom-AaptXmlTree {
    # The dump's lines ("  E: name (line=n)" / "    A: ns:name(0xid)=value" / "N: prefix=uri") as nodes with their
    # depth (two spaces a level), attributes (name -> raw value text) and children. Returns the root nodes (the
    # manifest, normally one) as a list; $null when no element was read.
    param([string[]]$Lines)
    $roots = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.ArrayList
    $current = $null
    foreach ($raw in $Lines) {
        if ($raw -match '^(?<indent> *)E: (?<name>[A-Za-z0-9_.\-]+)(?: \(line=\d+\))?\s*$') {
            $depth = $Matches['indent'].Length / 2
            $node = [pscustomobject]@{ Name = $Matches['name']; Depth = $depth; Attributes = [ordered]@{}; Children = New-Object System.Collections.ArrayList; Parent = $null }
            while ($stack.Count -gt 0 -and $stack[$stack.Count - 1].Depth -ge $depth) { $stack.RemoveAt($stack.Count - 1) }
            if ($stack.Count -eq 0) { [void]$roots.Add($node) } else { $node.Parent = $stack[$stack.Count - 1]; [void]$stack[$stack.Count - 1].Children.Add($node) }
            [void]$stack.Add($node)
            $current = $node
        }
        elseif ($raw -match '^ *A: (?:(?<ns>\S+?):)?(?<name>[A-Za-z0-9_]+)(?:\(0x[0-9a-fA-F]+\))?=(?<value>.*)$') {
            if ($current -ne $null) { $current.Attributes[$Matches['name']] = $Matches['value'] }
        }
    }
    if ($roots.Count -eq 0) { return $null }
    return ,@($roots)
}

function ConvertFrom-AaptValue {
    # A dumped attribute value as a typed value: a quoted string with its Raw text -> the string; "true"/"false" ->
    # bool; a decimal or hexadecimal number, with or without "(type 0x..)" -> int64 (0xffffffff, a boolean's true in
    # some dumps, -> -1 -- see Test-AaptTrue); a resource reference (@0x...) -> the text; $null stays $null.
    param([string]$Text)
    if ($null -eq $Text) { return $null }
    $t = $Text.Trim()
    if ($t -match '^"(?<s>.*)" \(Raw: ".*"\)$') { return $Matches['s'] }
    if ($t -match '^"(?<s>.*)"$') { return $Matches['s'] }
    if ($t -ceq 'true') { return $true }
    if ($t -ceq 'false') { return $false }
    if ($t -match '^(?:\(type 0x[0-9a-fA-F]+\))?(?<n>-?\d+)$') { return [int64]$Matches['n'] }
    if ($t -match '^(?:\(type 0x[0-9a-fA-F]+\))?0x(?<h>[0-9a-fA-F]+)$') { $v = [uint64]::Parse($Matches['h'], 'AllowHexSpecifier'); if ($v -eq [uint64]4294967295) { return [int64](-1) }; return [int64]$v }
    return $t
}

function Test-AaptTrue { param($Value) return ($Value -is [bool] -and $Value) -or ($Value -is [int64] -and ($Value -eq -1 -or $Value -eq 1)) }
function Test-AaptFalse { param($Value) return ($Value -is [bool] -and -not $Value) -or ($Value -is [int64] -and $Value -eq 0) }

# Android identifiers (element names, actions, categories, activity and feature names) are case-sensitive: every comparison of one is -ceq / -ccontains.
function Get-ManifestChildren { param($Node, [string]$Name) return @($Node.Children | Where-Object { $_.Name -ceq $Name }) }
function Get-AttributeValue { param($Node, [string]$Name) if ($null -eq $Node -or -not $Node.Attributes.Contains($Name)) { return $null }; return ConvertFrom-AaptValue $Node.Attributes[$Name] }

function Test-DistributionManifest {
    # The checks ([ordered] name -> bool) and the facts ([ordered]) of a manifest tree against the expected levels.
    param($Root, [int]$MinSdk = 32, [int]$TargetSdk = 34, [string]$LaunchActivity = 'com.unity3d.player.UnityPlayerGameActivity')
    $checks = [ordered]@{}
    $facts = [ordered]@{}
    $manifest = $null
    if ($null -ne $Root) { $manifest = @($Root | Where-Object { $_.Name -ceq 'manifest' }) | Select-Object -First 1 }
    $checks['the manifest element was read'] = ($null -ne $manifest)
    if ($null -eq $manifest) {
        $names = @(
            ('uses-sdk minSdkVersion is ' + $MinSdk),
            ('uses-sdk targetSdkVersion is ' + $TargetSdk + ' (named, not the automatic level)'),
            'compileSdkVersion, when present, is at least the targetSdkVersion',
            'installLocation is present and auto (0)',
            ('one launch activity (the one with MAIN) and it is ' + $LaunchActivity),
            'the launch intent-filter holds MAIN and com.oculus.intent.category.VR',
            'the launch activity has excludeFromRecents=true',
            'uses-feature android.hardware.vr.headtracking under manifest with required=true and version=1')
        foreach ($k in $names) { $checks[$k] = $false }
        return @{ Checks = $checks; Facts = $facts }
    }

    # uses-sdk: one, under the manifest.
    $sdk = @(Get-ManifestChildren $manifest 'uses-sdk')
    $min = $null; $target = $null
    if ($sdk.Count -eq 1) { $min = Get-AttributeValue $sdk[0] 'minSdkVersion'; $target = Get-AttributeValue $sdk[0] 'targetSdkVersion' }
    $facts['uses-sdk'] = "elements $($sdk.Count); minSdkVersion $min; targetSdkVersion $target"
    $checks['uses-sdk minSdkVersion is ' + $MinSdk] = ($sdk.Count -eq 1) -and ($min -is [int64]) -and ($min -eq $MinSdk)
    $checks['uses-sdk targetSdkVersion is ' + $TargetSdk + ' (named, not the automatic level)'] = ($sdk.Count -eq 1) -and ($target -is [int64]) -and ($target -eq $TargetSdk)
    $compile = Get-AttributeValue $manifest 'compileSdkVersion'
    $facts['compileSdkVersion'] = $(if ($null -eq $compile) { 'absent' } else { "$compile" })
    $checks['compileSdkVersion, when present, is at least the targetSdkVersion'] = ($null -eq $compile) -or (($compile -is [int64]) -and ($compile -ge $TargetSdk))

    # installLocation: present, 0 (auto). Absent is not a pass.
    $location = Get-AttributeValue $manifest 'installLocation'
    $facts['installLocation'] = $(if ($null -eq $location) { 'absent' } else { "$location" })
    $checks['installLocation is present and auto (0)'] = ($location -is [int64]) -and ($location -eq 0)

    # The launch activity: under application, the activities whose own intent-filter holds the action MAIN.
    $application = @(Get-ManifestChildren $manifest 'application')
    $launchers = @()
    $activities = @()
    if ($application.Count -eq 1) {
        foreach ($activity in Get-ManifestChildren $application[0] 'activity') {
            $name = Get-AttributeValue $activity 'name'
            $activities += "$name"
            foreach ($filter in Get-ManifestChildren $activity 'intent-filter') {
                $actions = @(Get-ManifestChildren $filter 'action' | ForEach-Object { Get-AttributeValue $_ 'name' })
                if ($actions -ccontains 'android.intent.action.MAIN') { $launchers += [pscustomobject]@{ Activity = $activity; Name = "$name"; Filter = $filter }; break }
            }
        }
    }
    $facts['activities'] = ($activities -join ', ')
    $facts['activities with MAIN'] = (($launchers | ForEach-Object { $_.Name }) -join ', ')
    $checks['one launch activity (the one with MAIN) and it is ' + $LaunchActivity] = ($application.Count -eq 1) -and ($launchers.Count -eq 1) -and ($launchers[0].Name -ceq $LaunchActivity)
    $filterOk = $false; $excludeOk = $false
    if ($launchers.Count -eq 1 -and $launchers[0].Name -ceq $LaunchActivity) {
        $categories = @(Get-ManifestChildren $launchers[0].Filter 'category' | ForEach-Object { Get-AttributeValue $_ 'name' })
        $facts['launch intent-filter categories'] = ($categories -join ', ')
        $filterOk = ($categories -ccontains 'com.oculus.intent.category.VR')
        $exclude = Get-AttributeValue $launchers[0].Activity 'excludeFromRecents'
        $facts['launch activity excludeFromRecents'] = $(if ($null -eq $exclude) { 'absent' } else { "$exclude" })
        $excludeOk = Test-AaptTrue $exclude
    }
    $checks['the launch intent-filter holds MAIN and com.oculus.intent.category.VR'] = $filterOk
    $checks['the launch activity has excludeFromRecents=true'] = $excludeOk

    # headtracking: one uses-feature, a direct child of the manifest.
    $features = @(Get-ManifestChildren $manifest 'uses-feature' | Where-Object { (Get-AttributeValue $_ 'name') -ceq 'android.hardware.vr.headtracking' })
    $required = $null; $version = $null
    if ($features.Count -eq 1) { $required = Get-AttributeValue $features[0] 'required'; $version = Get-AttributeValue $features[0] 'version' }
    $facts['uses-feature android.hardware.vr.headtracking'] = "elements $($features.Count); required $required; version $version"
    $checks['uses-feature android.hardware.vr.headtracking under manifest with required=true and version=1'] = ($features.Count -eq 1) -and (Test-AaptTrue $required) -and ($version -is [int64]) -and ($version -eq 1)

    # Facts for the record: the permissions and the supported devices as they are now (their choice is another unit's).
    $facts['uses-permission'] = ((Get-ManifestChildren $manifest 'uses-permission' | ForEach-Object { Get-AttributeValue $_ 'name' }) -join ', ')
    $devices = @()
    if ($application.Count -eq 1) { $devices = @(Get-ManifestChildren $application[0] 'meta-data' | Where-Object { (Get-AttributeValue $_ 'name') -ceq 'com.oculus.supportedDevices' } | ForEach-Object { Get-AttributeValue $_ 'value' }) }
    $facts['com.oculus.supportedDevices'] = $(if ($devices.Count -eq 0) { 'absent' } else { $devices -join ' | ' })
    return @{ Checks = $checks; Facts = $facts }
}
