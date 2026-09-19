# 시드 캠페인 5 레인을 Claude 세션과 분리된 프로세스로 띄운다.
#
# 배경: 백그라운드 셸로 띄운 학습이 세션 종료 때마다 함께 죽어 며칠치 계산이 반복 손실됐다.
# Start-Process 로 독립 프로세스를 만들면 세션이 끊겨도 학습은 계속된다.
# 각 레인은 완료된 런을 로그로 판별해 건너뛰므로, 중단돼도 이 스크립트를 다시 실행하면 이어진다.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bash = "C:\Program Files\Git\bin\bash.exe"
if (-not (Test-Path $bash)) { $bash = "bash" }

New-Item -ItemType Directory -Force -Path (Join-Path $here "runs_seeds") | Out-Null

foreach ($lane in 1..5) {
    Start-Process -FilePath $bash `
        -ArgumentList "run_seed_campaign.sh", "$lane" `
        -WorkingDirectory $here `
        -WindowStyle Hidden
    Start-Sleep -Milliseconds 800
    "lane $lane launched"
}
