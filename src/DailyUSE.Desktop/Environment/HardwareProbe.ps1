$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
$issues = New-Object 'System.Collections.Generic.List[object]'
$cpu = $null
$graphics = @()

try {
    $processors = @(Get-CimInstance -ClassName Win32_Processor -Property Name, NumberOfCores, NumberOfLogicalProcessors -OperationTimeoutSec 3)
    if ($processors.Count -gt 0) {
        $cores = ($processors | Measure-Object -Property NumberOfCores -Sum).Sum
        $logical = ($processors | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
        $cpu = @{ Name = ($processors[0].Name).Trim(); PhysicalCores = [int]$cores; LogicalProcessors = [int]$logical }
    } else {
        $issues.Add(@{ Area = '处理器'; Message = '暂时无法读取处理器详情' })
    }
} catch {
    $issues.Add(@{ Area = '处理器'; Message = '暂时无法读取处理器详情' })
}

try {
    $graphics = @(Get-CimInstance -ClassName Win32_VideoController -Property Name, DriverVersion -OperationTimeoutSec 3 | ForEach-Object {
        @{ Name = $_.Name; DriverVersion = $_.DriverVersion }
    })
    if ($graphics.Count -eq 0) { $issues.Add(@{ Area = '显卡'; Message = '暂时无法读取显卡信息' }) }
} catch {
    $issues.Add(@{ Area = '显卡'; Message = '暂时无法读取显卡信息' })
}

@{ Cpu = $cpu; Graphics = $graphics; Issues = @($issues.ToArray()) } | ConvertTo-Json -Depth 5 -Compress
