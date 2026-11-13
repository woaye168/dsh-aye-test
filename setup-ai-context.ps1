<#
.SYNOPSIS
    Generate AI context files for WasiCore projects.
.DESCRIPTION
    Reads src/WasiCoreSDK.props, discovers SDK path, copies SDK docs/api/schemas
    into the project (version-checked), and writes:
      - ai/tools/*
      - .cursor/rules/project-context.mdc
      - .cursor/rules/wasicore-coding-rules.mdc
      - .cursor/rules/trigger-mcp-workflow.mdc
      - .cursor/skills/*
      - AGENTS.md
      - CLAUDE.md
#>
param(
    [string]$ProjectPath = (Get-Location).Path
)

$ErrorActionPreference = "Stop"
$ProjectPath = $ProjectPath.TrimEnd('\', '/')

function Add-GitIgnoreEntries {
    param(
        [string]$ProjectPath,
        [string[]]$Entries
    )

    $gitignorePath = Join-Path $ProjectPath ".gitignore"
    $content = ""
    if (Test-Path $gitignorePath) {
        $content = Get-Content -LiteralPath $gitignorePath -Raw -Encoding UTF8
    }

    $normalized = "`n" + ($content -replace "`r`n", "`n") + "`n"
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $Entries) {
        if (-not $normalized.Contains("`n$entry`n")) {
            $missing.Add($entry)
        }
    }

    if ($missing.Count -eq 0) {
        return
    }

    $trimmed = $content.TrimEnd()
    if ($trimmed.Length -gt 0) {
        $trimmed += "`n"
    }
    $block = @("", "# SCE generated AI context") + @($missing)
    ($trimmed + ($block -join "`n") + "`n") | Out-File -FilePath $gitignorePath -Encoding UTF8 -NoNewline
}

function Copy-FileIfExists {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )

    if (-not (Test-Path $SourcePath)) {
        Write-Warning "File not found: $SourcePath"
        return $false
    }

    $parentDir = Split-Path -Parent $DestinationPath
    if (-not (Test-Path $parentDir)) {
        New-Item -Path $parentDir -ItemType Directory -Force | Out-Null
    }
    Copy-Item -Path $SourcePath -Destination $DestinationPath -Force
    return $true
}

function Copy-DirectoryReplacing {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )

    if (-not (Test-Path $SourcePath)) {
        Write-Warning "Directory not found: $SourcePath"
        return $false
    }

    if (Test-Path $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Recurse -Force
    }

    $parentDir = Split-Path -Parent $DestinationPath
    if (-not (Test-Path $parentDir)) {
        New-Item -Path $parentDir -ItemType Directory -Force | Out-Null
    }
    Copy-Item -Path $SourcePath -Destination $DestinationPath -Recurse -Force
    return $true
}

function Remove-OldJunction {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            try {
                [System.IO.Directory]::Delete($Path)
            } catch {
                Remove-Item -LiteralPath $Path -Force
            }
            Write-Host "  Removed old junction: $Path" -ForegroundColor Yellow
        }
    }
}

function Copy-SdkDocs {
    param(
        [string]$SdkPath,
        [string]$DocsDir
    )

    $localVersionFile = Join-Path $DocsDir ".sdk-version"

    # SDK 路径本身含版本编号（如 .../wasicoresdk/169/wasicoresdk/），路径变则版本变
    $sdkVersion = $SdkPath.TrimEnd('\', '/')

    # Check local version
    if (Test-Path $localVersionFile) {
        $localVersion = (Get-Content $localVersionFile -Raw -Encoding UTF8).Trim()
        if ($localVersion -eq $sdkVersion) {
            Write-Host "  SDK docs up-to-date (version: $sdkVersion)" -ForegroundColor DarkGray
            return
        }
    }

    if (-not (Test-Path $DocsDir)) {
        New-Item -Path $DocsDir -ItemType Directory -Force | Out-Null
    }

    # Clean up old junctions
    $sdkDocsLink = Join-Path $DocsDir "sdk"
    $sdkApiLink = Join-Path $DocsDir "api"
    $sdkSchemasLink = Join-Path $DocsDir "schemas"
    $sdkResourcesLink = Join-Path $DocsDir "resources"
    Remove-OldJunction -Path $sdkDocsLink
    Remove-OldJunction -Path $sdkApiLink
    Remove-OldJunction -Path $sdkSchemasLink
    Remove-OldJunction -Path $sdkResourcesLink

    # Copy via robocopy /MIR
    $pairs = @(
        @{ Src = (Join-Path $SdkPath "docs") -replace '/', '\'; Dst = $sdkDocsLink -replace '/', '\' },
        @{ Src = (Join-Path $SdkPath "api") -replace '/', '\';  Dst = $sdkApiLink -replace '/', '\' },
        @{ Src = (Join-Path $SdkPath "schemas") -replace '/', '\'; Dst = $sdkSchemasLink -replace '/', '\' },
        @{ Src = (Join-Path $SdkPath "resources") -replace '/', '\'; Dst = $sdkResourcesLink -replace '/', '\' }
    )

    foreach ($pair in $pairs) {
        if (-not (Test-Path $pair.Src)) {
            Write-Warning "  Source not found, skipping: $($pair.Src)"
            continue
        }
        robocopy $pair.Src $pair.Dst /MIR /NJH /NJS /NDL /NP /NS /NC /NFL | Out-Null
        Write-Host "  Copied: $($pair.Src) -> $($pair.Dst)" -ForegroundColor Green
    }

    # Write version stamp
    $sdkVersion | Out-File -FilePath $localVersionFile -Encoding UTF8 -NoNewline
    Write-Host "  SDK docs version: $sdkVersion" -ForegroundColor Cyan
}

function Get-RelativeProjectPath {
    param(
        [string]$ProjectPath,
        [string]$Path
    )

    $projectPrefix = ($ProjectPath.TrimEnd('\', '/') + '\')
    if ($Path.StartsWith($projectPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return ($Path.Substring($projectPrefix.Length) -replace '\\', '/')
    }

    return ($Path -replace '\\', '/')
}

function Get-EditorInstallRootFromSdkPath {
    param([string]$SdkPath)

    $trimmedSdkPath = $SdkPath.TrimEnd('\', '/')
    if (-not (Test-Path $trimmedSdkPath)) {
        return $null
    }

    $current = Get-Item -LiteralPath $trimmedSdkPath -ErrorAction SilentlyContinue
    if (-not $current) {
        return $null
    }

    if ($current -is [System.IO.FileInfo]) {
        $current = $current.Directory
    }

    while ($current) {
        $clientLogDir = Join-Path $current.FullName "logs\client"
        $serverLogDir = Join-Path $current.FullName "logs\server"
        $luaLogDir = Join-Path $current.FullName "logs\lua"

        if ((Test-Path $clientLogDir) -or (Test-Path $serverLogDir) -or (Test-Path $luaLogDir)) {
            return $current.FullName
        }

        $current = $current.Parent
    }

    return $null
}

function Get-RuntimeLogContext {
    param([string]$SdkPath)

    $editorRoot = Get-EditorInstallRootFromSdkPath -SdkPath $SdkPath
    if ([string]::IsNullOrWhiteSpace($editorRoot)) {
        return @{
            EditorRoot   = "<editor-install-root>"
            ClientLogDir = "<editor-install-root>/logs/client"
            ServerLogDir = "<editor-install-root>/logs/server"
            LuaLogDir    = "<editor-install-root>/logs/lua"
        }
    }

    $editorRootUnix = $editorRoot -replace '\\', '/'
    return @{
        EditorRoot   = $editorRootUnix
        ClientLogDir = (($editorRoot + '\logs\client') -replace '\\', '/')
        ServerLogDir = (($editorRoot + '\logs\server') -replace '\\', '/')
        LuaLogDir    = (($editorRoot + '\logs\lua') -replace '\\', '/')
    }
}

function Add-CategoryReference {
    param(
        [System.Collections.IDictionary]$Map,
        [string]$Category,
        [string]$Reference
    )

    if ([string]::IsNullOrWhiteSpace($Category)) {
        return
    }

    if (-not $Map.Contains($Category)) {
        $Map[$Category] = New-Object System.Collections.Generic.List[string]
    }

    if (-not [string]::IsNullOrWhiteSpace($Reference) -and -not $Map[$Category].Contains($Reference)) {
        $Map[$Category].Add($Reference)
    }
}

function Get-ProjectDataOwnershipContent {
    param([string]$ProjectPath)

    $jsonCategories = @{}
    $codeDefinitions = @{}
    $patchHotspots = @{}

    $dataDir = Join-Path $ProjectPath "editor\data"
    if (Test-Path $dataDir) {
        Get-ChildItem -Path $dataDir -Directory -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            $normalized = $_.FullName -replace '\\', '/'
            if ($normalized -match '/ScopeData/(GameData[A-Za-z0-9_]+)(?:/|$)') {
                Add-CategoryReference -Map $jsonCategories -Category $Matches[1] -Reference (Get-RelativeProjectPath -ProjectPath $ProjectPath -Path $_.FullName)
            }
        }
    }

    $srcDir = Join-Path $ProjectPath "src"
    $newPattern = [regex]'new\s+(?:global::[A-Za-z0-9_:.]+\.)?(GameData[A-Za-z0-9_]+)\s*\('
    $patchAssignPattern = [regex]'ScopeData\.(GameData[A-Za-z0-9_]+)\.[A-Za-z0-9_\.]+\.Data(?:!|\?)?\.[A-Za-z0-9_]+\s*='
    $patchMutatePattern = [regex]'ScopeData\.(GameData[A-Za-z0-9_]+)\.[A-Za-z0-9_\.]+\.Data(?:!|\?)?\.[A-Za-z0-9_]+\.(Add|AddRange|Clear|Remove|RemoveAll|Insert|TryAdd|TryRemove|Enqueue|Push)\s*\('
    $patchCallPattern = [regex]'[A-Za-z0-9_]+\(\s*ScopeData\.(GameData[A-Za-z0-9_]+)\.[A-Za-z0-9_\.]+\.Data(?:!|\?)?(?:\s*,|\s*\))'

    if (Test-Path $srcDir) {
        Get-ChildItem -Path $srcDir -Recurse -Filter "*.cs" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(DataGenerated|TriggerGenerated|bin|obj)\\' } |
            ForEach-Object {
                $relativePath = Get-RelativeProjectPath -ProjectPath $ProjectPath -Path $_.FullName
                $lineNumber = 0
                foreach ($line in Get-Content -Path $_.FullName -Encoding UTF8) {
                    $lineNumber++

                    foreach ($match in $newPattern.Matches($line)) {
                        Add-CategoryReference -Map $codeDefinitions -Category $match.Groups[1].Value -Reference "${relativePath}:$lineNumber"
                    }

                    $assignMatch = $patchAssignPattern.Match($line)
                    if ($assignMatch.Success) {
                        Add-CategoryReference -Map $patchHotspots -Category $assignMatch.Groups[1].Value -Reference "${relativePath}:$lineNumber"
                        continue
                    }

                    $mutateMatch = $patchMutatePattern.Match($line)
                    if ($mutateMatch.Success) {
                        Add-CategoryReference -Map $patchHotspots -Category $mutateMatch.Groups[1].Value -Reference "${relativePath}:$lineNumber"
                        continue
                    }

                    $callMatch = $patchCallPattern.Match($line)
                    if ($callMatch.Success) {
                        Add-CategoryReference -Map $patchHotspots -Category $callMatch.Groups[1].Value -Reference "${relativePath}:$lineNumber"
                    }
                }
            }
    }

    $allCategorySet = @{}
    foreach ($key in $jsonCategories.Keys) {
        $allCategorySet[[string]$key] = $true
    }
    foreach ($key in $codeDefinitions.Keys) {
        $allCategorySet[[string]$key] = $true
    }
    foreach ($key in $patchHotspots.Keys) {
        $allCategorySet[[string]$key] = $true
    }
    $allCategories = $allCategorySet.Keys | Sort-Object

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('---')
    [void]$sb.AppendLine('description: 编辑器自动生成的项目数据归属摘要，帮助 AI 判断 GameData 应该改 editor/data 还是代码定义。')
    [void]$sb.AppendLine('globs:')
    [void]$sb.AppendLine('  - "src/**/*.cs"')
    [void]$sb.AppendLine('  - "editor/data/**/*.json"')
    [void]$sb.AppendLine('alwaysApply: false')
    [void]$sb.AppendLine('---')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('<!-- 本文件由 setup-ai-context 自动生成，请勿手动编辑。重新运行 AI 上下文初始化后会刷新。 -->')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('# 项目数据归属')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 工作规则')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- `editor/data/**` 与 `data_*` MCP 是同一路数据源编辑方式。')
    [void]$sb.AppendLine('- `src/**/*.cs` 中 `new GameDataXxx(...)` 属于代码定义路线。')
    [void]$sb.AppendLine('- 同一张 GameData 表只能选择一个静态来源；不要对 json-backed 条目再做 `ScopeData.GameDataXxx.*.Data` 静态补丁。')
    [void]$sb.AppendLine('- `src/DataGenerated/**` 是生成投影，只用于查 Link、确认 `ScopeData.*` 名称和查看生成结果。')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 检测摘要')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| GameData 类别 | editor/data | 代码定义 (`new GameDataXxx`) | 可疑 `.Data` 静态补丁 |')
    [void]$sb.AppendLine('|---|---|---|---|')

    if ($allCategories.Count -eq 0) {
        [void]$sb.AppendLine('| — | 未检测到 | 未检测到 | 未检测到 |')
    } else {
        foreach ($category in $allCategories) {
            $jsonText = if ($jsonCategories.Contains($category)) { 'yes' } else { '—' }
            $codeCount = if ($codeDefinitions.Contains($category)) { $codeDefinitions[$category].Count } else { 0 }
            $patchCount = if ($patchHotspots.Contains($category)) { $patchHotspots[$category].Count } else { 0 }
            [void]$sb.AppendLine("| $category | $jsonText | $codeCount | $patchCount |")
        }
    }

    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 可疑 mixed-ownership 提示')
    [void]$sb.AppendLine()

    $warningCategories = @($allCategories | Where-Object { $jsonCategories.Contains($_) -and $patchHotspots.Contains($_) })
    if ($warningCategories.Count -eq 0) {
        [void]$sb.AppendLine('- 未检测到明显的 json-backed 类别与 .Data 静态补丁组合。')
    } else {
        foreach ($category in $warningCategories) {
            [void]$sb.AppendLine("- ${category}: 该类别在 editor/data 下已有源数据，同时代码里出现了可疑 .Data 静态补丁。优先回头确认是否应统一回 json / MCP 路线。")
            foreach ($reference in $patchHotspots[$category] | Select-Object -First 8) {
                [void]$sb.AppendLine("  - $reference")
            }
        }
    }

    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 说明')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- 代码定义通过搜索 new GameDataXxx(...) 得到，只用于提示该类别存在代码 authoring。')
    [void]$sb.AppendLine('- 可疑 .Data 静态补丁是启发式扫描结果，主要覆盖字段赋值、常见集合改写，以及把 .Data 作为 helper 参数传递的情况。')

    return $sb.ToString()
}

# 1) Read SDK path
$propsFile = Join-Path $ProjectPath "src\WasiCoreSDK.props"
if (-not (Test-Path $propsFile)) {
    Write-Error "Missing src/WasiCoreSDK.props. Open project once in editor first. Path: $propsFile"
    exit 1
}

[xml]$propsXml = Get-Content $propsFile -Encoding UTF8
$sdkPath = $propsXml.Project.PropertyGroup.WasiCoreSDKPath
if ([string]::IsNullOrWhiteSpace($sdkPath)) {
    Write-Error "WasiCoreSDKPath not found in src/WasiCoreSDK.props."
    exit 1
}

$sdkPath = $sdkPath.TrimEnd('\', '/') + '/'
Write-Host "SDK Path: $sdkPath" -ForegroundColor Cyan

# 1b) Detect csproj
$srcDir = Join-Path $ProjectPath "src"
$csprojFiles = @(Get-ChildItem -Path $srcDir -Filter "*.csproj" -File -ErrorAction SilentlyContinue)
if ($csprojFiles.Count -eq 0) {
    $csprojName = "GameEntry.csproj"
    Write-Warning "No .csproj found in src/. Using GameEntry.csproj."
} elseif ($csprojFiles.Count -eq 1) {
    $csprojName = $csprojFiles[0].Name
} else {
    $preferredCsproj = $csprojFiles | Where-Object { $_.Name -eq "GameEntry.csproj" } | Select-Object -First 1
    if ($preferredCsproj) {
        $csprojName = $preferredCsproj.Name
    } else {
        $csprojName = $csprojFiles[0].Name
    }
    Write-Warning "Multiple .csproj files found. Using: $csprojName"
}
Write-Host "Project: src/$csprojName" -ForegroundColor Cyan

# 2) Copy SDK docs (version-checked)
$docsLinkDir = Join-Path $ProjectPath "docs"
Write-Host "Syncing SDK documentation..." -ForegroundColor Cyan
Copy-SdkDocs -SdkPath $sdkPath.TrimEnd('/') -DocsDir $docsLinkDir

# 2b) Copy examples index from map_templates (derived from SDK path)
$sdkTrimmed = $sdkPath.TrimEnd('\', '/')
$resMDir = Split-Path (Split-Path (Split-Path (Split-Path $sdkTrimmed -Parent) -Parent) -Parent) -Parent
$mapTemplatesParent = Join-Path $resMDir "map_templates"
$mapTemplatesPath = $null

if (Test-Path $mapTemplatesParent) {
    $versionDirs = @(Get-ChildItem -Path $mapTemplatesParent -Directory -ErrorAction SilentlyContinue)
    foreach ($vdir in $versionDirs) {
        $candidate = Join-Path $vdir.FullName "map_templates"
        if (Test-Path $candidate) {
            $mapTemplatesPath = ($candidate -replace '\\', '/') + '/'
            break
        }
    }
}

if ($mapTemplatesPath) {
    $examplesIndexSrc = Join-Path $mapTemplatesPath "code_sample/src/EXAMPLES_INDEX.md"
    if (Test-Path $examplesIndexSrc) {
        if (-not (Test-Path $docsLinkDir)) {
            New-Item -Path $docsLinkDir -ItemType Directory -Force | Out-Null
        }
        $examplesContent = Get-Content $examplesIndexSrc -Raw -Encoding UTF8
        $examplesPath = "${mapTemplatesPath}code_sample/src/"
        $headerLine1 = '> 示例源码位置: `' + $examplesPath + '`'
        $headerLine2 = '> 按下方索引定位示例后，用 Read 工具从上述目录中读取对应子目录的源码。'
        $headerLine3 = '> **技能文档优先**：若任务与 `docs/sdk/ai/skills/` 下某专题匹配（如 3D 单位与战斗：`docs/sdk/ai/skills/3d-unit-game/SKILL.md`），请先阅读该 `SKILL.md` 的推荐模式，再参考完整示例。'
        $headerLine4 = '> **冲突时以技能文档为准**：索引中的完整游戏示例可能沿用旧习惯或与最新文档不一致，实现细节以对应 `SKILL.md` / `reference.md` 为准。'
        $header = $headerLine1 + "`n" + $headerLine2 + "`n" + $headerLine3 + "`n" + $headerLine4 + "`n`n"
        ($header + $examplesContent) | Out-File -FilePath (Join-Path $docsLinkDir "examples-index.md") -Encoding UTF8 -NoNewline
        Write-Host "  Copied examples index from: $examplesIndexSrc" -ForegroundColor Green
    }
} else {
    Write-Host "  map_templates not found near SDK path, skipping examples index" -ForegroundColor DarkGray
}

# 3) Write .cursor/rules and AI context files from SDK templates
$generatedIgnoreEntries = @(
    "/docs/sdk",
    "/docs/api",
    "/docs/schemas",
    "/docs/resources",
    "/docs/.sdk-version",
    "/docs/.editor-root",
    "/docs/.editor-api-version",
    "/docs/.editor-launcher",
    "/docs/.client-only-debug.json",
    "/docs/examples-index.md",
    "/ai/tools/",
    "/.cursor/tools/",
    "/.cursor/skills/",
    "/.cursor/rules/project-context.mdc",
    "/.cursor/rules/wasicore-coding-rules.mdc",
    "/.cursor/rules/project-data-ownership.mdc",
    "/.cursor/rules/project-info.mdc",
    "/.cursor/rules/trigger-mcp-workflow.mdc",
    "/AGENTS.md",
    "/CLAUDE.md"
)
Add-GitIgnoreEntries -ProjectPath $ProjectPath -Entries $generatedIgnoreEntries

$aiToolsDir = Join-Path $ProjectPath "ai\tools"
$cursorRulesDir = Join-Path $ProjectPath ".cursor\rules"
$cursorToolsDir = Join-Path $ProjectPath ".cursor\tools"
$cursorSkillsDir = Join-Path $ProjectPath ".cursor\skills"
New-Item -Path $aiToolsDir -ItemType Directory -Force | Out-Null
New-Item -Path $cursorRulesDir -ItemType Directory -Force | Out-Null
New-Item -Path $cursorToolsDir -ItemType Directory -Force | Out-Null
New-Item -Path $cursorSkillsDir -ItemType Directory -Force | Out-Null

$templatesDir = Join-Path $sdkPath "templates"
if (-not (Test-Path $templatesDir)) {
    Write-Error "Templates directory not found: $templatesDir"
    exit 1
}

$sdkPathUnix = $sdkPath -replace '\\', '/'
$runtimeLogContext = Get-RuntimeLogContext -SdkPath $sdkPath

function Apply-Template {
    param(
        [string]$TemplatePath,
        [string]$DestPath
    )
    if (-not (Test-Path $TemplatePath)) {
        Write-Warning "Template not found: $TemplatePath"
        return
    }
    $content = Get-Content $TemplatePath -Raw -Encoding UTF8
    $content = $content -replace '\{\{SDK_PATH\}\}', $sdkPathUnix
    $content = $content -replace '\{\{CSPROJ_NAME\}\}', $csprojName
    $content = $content -replace '\{\{EDITOR_ROOT\}\}', $runtimeLogContext.EditorRoot
    $content = $content -replace '\{\{EDITOR_LOG_CLIENT_DIR\}\}', $runtimeLogContext.ClientLogDir
    $content = $content -replace '\{\{EDITOR_LOG_SERVER_DIR\}\}', $runtimeLogContext.ServerLogDir
    $content = $content -replace '\{\{EDITOR_LOG_LUA_DIR\}\}', $runtimeLogContext.LuaLogDir
    $content | Out-File -FilePath $DestPath -Encoding UTF8 -NoNewline
}

Apply-Template -TemplatePath (Join-Path $templatesDir "project-context.mdc.template") -DestPath (Join-Path $cursorRulesDir "project-context.mdc")
Apply-Template -TemplatePath (Join-Path $templatesDir "AGENTS.md.template") -DestPath (Join-Path $ProjectPath "AGENTS.md")
Apply-Template -TemplatePath (Join-Path $templatesDir "CLAUDE.md.template") -DestPath (Join-Path $ProjectPath "CLAUDE.md")

$projectDataOwnershipPath = Join-Path $cursorRulesDir "project-data-ownership.mdc"
(Get-ProjectDataOwnershipContent -ProjectPath $ProjectPath) | Out-File -FilePath $projectDataOwnershipPath -Encoding UTF8 -NoNewline

$codingRulesTemplate = Join-Path $templatesDir "wasicore-coding-rules.mdc"
if (Test-Path $codingRulesTemplate) {
    Copy-Item -Path $codingRulesTemplate -Destination (Join-Path $cursorRulesDir "wasicore-coding-rules.mdc") -Force
} else {
    Write-Warning "Template not found: $codingRulesTemplate"
}

$triggerWorkflow = Join-Path $sdkPath "docs\ai\rules\trigger-mcp-workflow.mdc"
Copy-FileIfExists -SourcePath $triggerWorkflow -DestinationPath (Join-Path $cursorRulesDir "trigger-mcp-workflow.mdc") | Out-Null

$triggerSkill = Join-Path $sdkPath "docs\ai\skills\trigger-editor-mcp"
Copy-DirectoryReplacing -SourcePath $triggerSkill -DestinationPath (Join-Path $cursorSkillsDir "trigger-editor-mcp") | Out-Null

$clientOnlySkill = Join-Path $sdkPath "docs\ai\skills\client-only-debug"
Copy-DirectoryReplacing -SourcePath $clientOnlySkill -DestinationPath (Join-Path $cursorSkillsDir "client-only-debug") | Out-Null

$triggerMcpTool = Join-Path $sdkPath "docs\ai\tools\Invoke-SceTriggerMcp.ps1"
Copy-FileIfExists -SourcePath $triggerMcpTool -DestinationPath (Join-Path $aiToolsDir "Invoke-SceTriggerMcp.ps1") | Out-Null
Copy-FileIfExists -SourcePath $triggerMcpTool -DestinationPath (Join-Path $cursorToolsDir "Invoke-SceTriggerMcp.ps1") | Out-Null

$clientOnlyTool = Join-Path $sdkPath "docs\ai\tools\Start-SceClientOnlyDebug.ps1"
Copy-FileIfExists -SourcePath $clientOnlyTool -DestinationPath (Join-Path $aiToolsDir "Start-SceClientOnlyDebug.ps1") | Out-Null
Copy-FileIfExists -SourcePath $clientOnlyTool -DestinationPath (Join-Path $cursorToolsDir "Start-SceClientOnlyDebug.ps1") | Out-Null

Write-Host ""
Write-Host "=== AI Context Setup Complete ===" -ForegroundColor Magenta
Write-Host "  docs/sdk/                                (SDK docs, copied)" -ForegroundColor White
Write-Host "  docs/api/                                (SDK API declarations, copied)" -ForegroundColor White
Write-Host "  docs/schemas/                            (SDK schemas, copied)" -ForegroundColor White
Write-Host "  docs/resources/                          (asset index, copied)" -ForegroundColor White
if ($mapTemplatesPath) {
    Write-Host "  docs/examples-index.md                   (examples index, copied)" -ForegroundColor White
}
Write-Host "  ai/tools/                                (AI helper scripts)" -ForegroundColor White
Write-Host "  .cursor/rules/project-context.mdc        (Cursor auto-applied rule)" -ForegroundColor White
Write-Host "  .cursor/rules/project-data-ownership.mdc (data ownership summary)" -ForegroundColor White
Write-Host "  .cursor/rules/trigger-mcp-workflow.mdc   (Trigger MCP workflow)" -ForegroundColor White
Write-Host "  .cursor/skills/                          (Cursor skill mirrors)" -ForegroundColor White
Write-Host "  .cursor/tools/                           (Cursor tool mirrors)" -ForegroundColor White
Write-Host "  Runtime client logs:                     $($runtimeLogContext.ClientLogDir)" -ForegroundColor White
Write-Host "  Runtime server logs:                     $($runtimeLogContext.ServerLogDir)" -ForegroundColor White
Write-Host "  Editor Lua logs:                         $($runtimeLogContext.LuaLogDir)" -ForegroundColor White
Write-Host "  .cursor/rules/wasicore-coding-rules.mdc  (Cursor coding rules for C#)" -ForegroundColor White
Write-Host "  AGENTS.md                                (IDE-agnostic AI discovery)" -ForegroundColor White
Write-Host "  CLAUDE.md                                (Claude Code project instructions)" -ForegroundColor White
Write-Host ""

