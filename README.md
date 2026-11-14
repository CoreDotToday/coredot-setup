# 코어닷 관리센터 (coredot-setup)

키오스크 PC 에 필요한 **프린터 드라이버 · 프로그램 · 코어닷프린터를 한 번에 설치하고, 이후 업데이트와 새 프로그램까지 알아서 챙기는** 설치기 겸 관리 프로그램입니다.

## 새 PC 에 설치

1. **[CoreDotSetup.exe 받기](https://github.com/CoreDotToday/coredot-setup/releases/latest/download/CoreDotSetup.exe)** (로그인 필요 없음)
2. 실행 → 관리자 확인 [예] → **[설치]**
   - 코드서명이 없어 처음엔 파란 경고가 뜰 수 있습니다: [추가 정보] → [실행]
3. 끝나면 바탕 화면에 **코어닷 관리센터** · **코어닷프린터** 가 생깁니다. 프린터 USB 는 설치 후에 꽂아도 됩니다.

설치되는 것 — [`catalog.json`](catalog.json) 에 적힌 전부:

| 항목 | 종류 |
|---|---|
| Epson L8050 · L18050 | 프린터 드라이버 |
| Evolis 카드 프린터 (Agilia 등) | 프린터 드라이버 |
| IDP SMART-32 | 프린터 드라이버 |
| Logi Tune | 로지텍 카메라 · 헤드셋 프로그램 |
| 코어닷프린터 | [coredot-printer-v2](https://github.com/CoreDotToday/coredot-printer-v2) 최신 릴리스 |

Windows 10 / 11 64비트, 인터넷 필요. 창 없이 설치: `CoreDotSetup.exe --setup /silent`

## 설치 후: 코어닷 관리센터

- **한 창에서 관리**: 항목마다 설치된 판 · 프린터 연결 상태 · [열기] [인쇄 설정] [다시 설치] · 제조사 도구 버튼
- **자동 알림**: 로그인 2분 뒤 · 3시간마다 확인해서 새 버전이나 **새로 추가된 프로그램** 이 있으면 알림 창 → [지금 설치]
- **관리자 확인 없이 설치**: 설치는 SYSTEM 권한의 작업(`\CoreDot\Installer`)이 합니다
- **스스로 업데이트**: 관리센터 새 판도 같은 알림으로 바뀝니다
- 문제 해결: 인쇄 대기열 비우기 · 프린터 설정 · 로그 폴더
- 제거: 제어판 › 프로그램 › **코어닷 관리센터** (드라이버 · 프로그램 · 코어닷프린터는 남김)

## 프로그램 추가 · 업데이트 (관리자용)

모든 PC 는 `main` 브랜치의 **`catalog.json`** 을 봅니다. 이 파일을 바꿔 푸시하면 3시간 안에 모든 키오스크에 알림이 뜹니다.

```powershell
# 1) 설치 파일 넣기 (git 에는 안 올라감)
#    드라이버: drivers\<id>\  — 드라이버가 깔린 PC 에서 tools\export-drivers.ps1 로 꺼낼 수 있음
#    프로그램: extras\<파일>.exe
# 2) 새 프로그램이면 catalog.json 의 items 에 항목 추가 (아래 예시)
# 3) 묶어서 올리고 catalog.json 채우기 (판 번호 · 주소 · SHA256 · 크기 자동)
powershell -ExecutionPolicy Bypass -File tools\publish-packages.ps1 -Upload
# 4) git commit catalog.json → git push
```

`catalog.json` 항목 예시:

```json
{ "id": "logitune", "name": "Logi Tune", "kind": "exe",
  "source": "extras/LogiTuneInstall.exe",
  "args": "/quiet /norestart",            // 창 없이 설치하는 인자 (설치 파일 종류마다 다름)
  "detect": "Logi Tune",                  // 제어판 프로그램 목록에 보이는 이름 (설치 여부 · 판 확인)
                                          //  끝에 * 를 붙이면 그 앞부분으로 시작하는 이름 (예: "Evolis Premium Suite 2*")
  "rebootCodes": [500],                   // (선택) '설치됨 · 재부팅 필요' 로 볼 종료 코드 — 3010 · 1641 은 기본
  "open": "%ProgramFiles%\\Logitech\\LogiTune\\LogiTune.exe" }

{ "id": "epson-l8050", "name": "Epson L8050", "kind": "driver",
  "source": "drivers/epson-l8050",
  "inf": "e1wf1b0e.inf",                  // 드라이버 저장소에서 판을 찾을 INF 이름
  "printers": ["EPSON L8050 Series"],     // 인쇄 드라이버 이름 (상태 표시 · 인쇄 설정 버튼)
  "tools": [{ "label": "도구", "path": "..." }] }
```

`version` · `url` · `sha256` · `size` 는 `publish-packages.ps1` 이 채웁니다. 관리센터는 **SHA256 이 맞는 파일만** 설치합니다.

### 관리센터 새 판 내기

```powershell
# src\Program.cs 의 Version 을 올린 뒤
powershell -ExecutionPolicy Bypass -File tools\publish-manager.ps1 -Notes "바뀐 점"
git commit catalog.json; git push   # → 모든 PC 의 관리센터가 새 판으로
```

## 구조

```
src\Program.cs     하나의 exe: 받은 자리에서 실행 = 설치, 설치된 자리에서 실행 = 관리센터,
                   --check = 알림, --service = 백그라운드 설치(SYSTEM), --uninstall = 제거
src\Catalog.cs     catalog.json · 설치된 판 찾기 · 할 일 계산
src\Engine.cs      다운로드 · 검증 · 드라이버(pnputil) · 프로그램 설치, SYSTEM 서비스
src\ManagerUI.cs   관리센터 창 · 알림 창 · 진행 창
src\SetupMode.cs   첫 설치 · 작업 스케줄러 · 바로 가기 · 제어판 · 예전 코어닷프린터 자리에서 옮겨 오기 · 제거
src\Common.cs      공용 (로그 · HTTP · 해시 · 코어닷프린터 업데이트)
build.ps1          → dist\CoreDotSetup.exe (윈도우 기본 .NET Framework 4 csc, 설치할 것 없음)
tools\             publish-packages · publish-manager · export-drivers
```

설치된 PC 에서:

| 자리 | 내용 | 권한 |
|---|---|---|
| `C:\Program Files\CoreDot\CoreDotManager.exe` | 관리센터 | 관리자만 쓰기 |
| `C:\ProgramData\CoreDot\` | 목록 · 받은 설치 파일 · 서비스 로그 | SYSTEM · 관리자만 쓰기 |
| `C:\ProgramData\CoreDot\requests\` | 관리센터 → 서비스 '이것 설치해 줘' (id 만) | 사용자 쓰기 |
| `C:\CoreDotPrinter\` | 코어닷프린터 exe (사용자 권한으로 교체) | 사용자 쓰기 |
| 작업 스케줄러 `\CoreDot\Installer` · `\CoreDot\Check` | 설치(SYSTEM) · 확인(로그인 사용자) | |

보안: 서비스는 사용자에게서 **항목 id 만** 받고, 목록 · 파일은 직접 GitHub 에서 받아 SHA256 을 확인한 뒤 SYSTEM 만 쓸 수 있는 폴더에서 실행합니다.
