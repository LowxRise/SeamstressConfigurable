param(
    [string]$ProfilePath = "$env:APPDATA\r2modmanPlus-local\RiskOfRain2\profiles\Singleplayer",
    [Parameter(Mandatory = $true)][string]$ManagedPath
)

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $ProfilePath 'BepInEx\core\Mono.Cecil.dll')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$project = Split-Path $PSScriptRoot
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($ManagedPath)
$resolver.AddSearchDirectory((Join-Path $ProfilePath 'BepInEx\core'))
$parameters = [Mono.Cecil.ReaderParameters]::new()
$parameters.AssemblyResolver = $resolver
$seamstress = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ProfilePath 'BepInEx\plugins\tsuyoikenko-Seamstress\SeamstressMod.dll'), $parameters)
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ManagedPath 'RoR2.dll'), $parameters)
$addon = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $project 'bin\Release\netstandard2.1\SeamstressConfigurable.dll'), $parameters)
$script:checks = 0

function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
    $script:checks++
    Write-Output "PASS $Name"
}

function Method($Assembly, [string]$Type, [string]$Name) {
    $result = @($Assembly.MainModule.GetType($Type).Methods | Where-Object Name -eq $Name)
    if ($result.Count -ne 1) { throw "Expected one $Type.$Name" }
    return $result[0]
}

$targets = @(
    @('SeamstressMod.Seamstress.SkillStates.Trim', 'OnEnter', @('trimDamageCoefficient', 'trimThirdDamageCoefficient'), 2),
    @('SeamstressMod.Seamstress.SkillStates.Flurry', 'OnEnter', @('flurryDamageCoefficient'), 1),
    @('SeamstressMod.Modules.BaseStates.BaseMeleeAttack', 'FixedUpdate', @('scissorSlashDamageCoefficient'), 1),
    @('SeamstressMod.Seamstress.SkillStates.Telekinesis', 'FixedUpdate', @('telekinesisDamageCoefficient'), 4),
    @('SeamstressMod.Seamstress.Components.DetonateOnImpactThrownTelekinesis', 'FixedUpdate', @('telekinesisDamageCoefficient'), 2),
    @('SeamstressMod.Seamstress.Components.ScissorImpact', 'OnProjectileImpact', @('scissorDamageCoefficient'), 1)
)
foreach ($target in $targets) {
    $method = Method $seamstress $target[0] $target[1]
    $reads = @($method.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldsfld' -and $_.Operand.Name -in $target[2] })
    Check ($reads.Count -eq $target[3]) "$($target[0]).$($target[1]) damage-read count"
    Check (@($reads | Where-Object { $_.Next.Operand.Name -ne 'get_Value' -or $_.Next.Operand.DeclaringType.GenericArguments[0].FullName -ne 'System.Single' }).Count -eq 0) "$($target[0]) getter signatures"
}
$clip = Method $seamstress 'SeamstressMod.Seamstress.SkillStates.Clip' 'OnEnter'
Check (@($clip.DeclaringType.Fields | Where-Object { $_.Name -eq 'damageCoefficient' -and $_.FieldType.FullName -eq 'System.Single' }).Count -eq 1) 'Clip cached damage field'
$melee = $seamstress.MainModule.GetType('SeamstressMod.Modules.BaseStates.BaseMeleeAttack')
Check (@($melee.Fields | Where-Object { $_.Name -eq 'swingIndex' -and $_.FieldType.FullName -eq 'System.Int32' }).Count -eq 1) 'Trim third-hit selection field'
$blast = Method $game 'RoR2.Projectile.ProjectileExplosion' 'DetonateServer'
$damageReads = @($blast.Body.Instructions | Where-Object {
    $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'blastDamageCoefficient' -and
    $_.Next.OpCode.Name -eq 'mul' -and $_.Next.Next.OpCode.Name -eq 'stfld' -and $_.Next.Next.Operand.Name -eq 'baseDamage'
})
Check ($damageReads.Count -eq 1) 'Pickup damage hook matches once'
Check (@($blast.Body.Instructions | Where-Object { $_.Operand.Name -eq 'blastDamageCoefficient' -and $_.Next.Next.Operand.Name -eq 'baseForce' }).Count -eq 1) 'Separate blast-force calculation remains untouched'
$config = $seamstress.MainModule.GetType('SeamstressMod.Seamstress.Content.SeamstressConfig')
foreach ($field in @('trimDamageCoefficient','trimThirdDamageCoefficient','flurryDamageCoefficient','scissorSlashDamageCoefficient','clipDamageCoefficient','telekinesisDamageCoefficient','scissorDamageCoefficient','scissorPickupDamageCoefficient')) {
    Check (@($config.Fields | Where-Object { $_.Name -eq $field -and $_.FieldType.FullName -eq 'BepInEx.Configuration.ConfigEntry`1<System.Single>' }).Count -eq 1) "Original default available for $field"
}
$lifesteal = Method $seamstress 'SeamstressMod.Seamstress.Components.SeamstressBaseDamageController' 'ApplyLifesteal'
Check (@($lifesteal.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldsfld' -and $_.Operand.Name -eq 'passiveLifeSteal' }).Count -eq 1) 'Existing lifesteal hook'
$skewer = Method $seamstress 'SeamstressMod.Seamstress.SkillStates.FireScissor' 'OnEnter'
Check (@($skewer.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and [Math]::Abs($_.Operand - 0.15) -lt 0.000001 }).Count -eq 1) 'Existing Skewer health-cost hook'
foreach ($member in $addon.MainModule.GetMemberReferences()) {
    if ($member.DeclaringType.Scope.Name -ne 'RoR2') { continue }
    if ($member -is [Mono.Cecil.MethodReference] -or $member -is [Mono.Cecil.FieldReference]) {
        $resolved = $member.Resolve()
        Check ($null -ne $resolved -and $resolved.IsPublic) "Current game API: $($member.Name)"
    }
}
$plugin = $addon.MainModule.GetType('SeamstressConfigurable.Plugin')
$pluginVersion = ($plugin.CustomAttributes | Where-Object { $_.AttributeType.Name -eq 'BepInPlugin' }).ConstructorArguments[2].Value
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $project 'Thunderstore\manifest.json') | ConvertFrom-Json
Check ($manifest.version_number -eq $pluginVersion -and $addon.Name.Version.ToString(3) -eq $pluginVersion) 'Manifest, plugin and assembly versions agree'
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $project "SeamstressConfigurable-$pluginVersion.zip"))
try {
    foreach ($entry in @('manifest.json','README.md','CHANGELOG.md','icon.png','plugins/SeamstressConfigurable/SeamstressConfigurable.dll')) {
        Check ($null -ne $zip.GetEntry($entry)) "Package contains $entry"
    }
    Check ($zip.Entries.Count -eq 5) 'Package contains only the intended five files'
}
finally { $zip.Dispose() }
$seamstress.Dispose()
$game.Dispose()
$addon.Dispose()
$resolver.Dispose()
Write-Output "$script:checks static checks passed. In-game testing is still needed."
