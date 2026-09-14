# ECS

여러 Claude 계정의 사용량을 한 화면에서 보는 Windows 데스크톱 위젯.

항상 위에 떠 있는 작은 패널에 계정별 5시간 창과 주간 창의 사용률, 남은 시간, 지금 돌고
있는 Claude Code 세션이 표시됩니다. 계정마다 `claude1`, `claude2` 같은 명령이 자동으로
만들어져서 어느 터미널에서든 바로 그 계정으로 들어갈 수 있습니다.

> **비공식 도구입니다.** 사용량은 Claude Code가 `/usage`에 쓰는 것과 같은 엔드포인트에서
> 가져오는데, 이 엔드포인트는 Anthropic 문서에 없습니다. 예고 없이 바뀌거나 막힐 수 있고
> 그러면 사용량 표시가 동작하지 않습니다. 계정 관리와 세션 표시는 로컬 파일만 읽으므로
> 영향을 받지 않습니다.

## 요구 사항

- Windows 10 이상 (64비트)
- [Claude Code](https://claude.com/claude-code) 설치 및 로그인
- .NET 8 Desktop Runtime — `ECS-standalone.exe`를 쓰면 필요 없습니다

## 설치

[Releases](../../releases)에서 내려받으세요.

| 파일 | 크기 | 설명 |
|---|---|---|
| `ECS.exe` | 0.2 MB | .NET 8 Desktop Runtime 필요 |
| `ECS-standalone.exe` | 59 MB | 런타임 포함, 그대로 실행 |

받은 파일을 아무 곳에나 두고 실행하면 됩니다. 설치 과정은 없습니다. 소스에서 빌드하려면
`install.ps1`이 `%LOCALAPPDATA%\ECS`로 복사하고 시작 메뉴에 등록해 줍니다.

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
.\install.ps1
```

## 쓰는 법

패널은 **아무 곳이나 클릭하면 모든 계정을 갱신**합니다. 위쪽 가운데의 짧은 막대를 잡고
끌면 창이 움직입니다.

나머지 기능은 트레이 아이콘 우클릭에 있습니다.

```
Refresh now
Add account…
Account order     ▸  순서를 바꾸면 claude1..N 번호도 따라갑니다
Delete account    ▸
Refresh interval  ▸  Manual only / 1 / 3 / 5 / 10 / 30 분
Always on top
Start with Windows
Quit
```

아이콘 왼쪽 클릭은 패널을 숨기고 다시 보여줍니다.

### 계정 추가

`Add account…`에서 이름만 입력하면 `~/.claude-accounts/<이름>`을 만들고 로그인 터미널을
엽니다. 거기서 `/login`을 실행하면 끝입니다.

새 계정은 기본 계정 `~/.claude`의 설정을 그대로 물려받습니다.

| 항목 | 방식 |
|---|---|
| skills, agents, commands, plugins | 정션으로 공유 — 한 곳만 고치면 전부 반영 |
| projects (대화 기록) | 정션으로 공유 |
| `CLAUDE.md` | `@` 임포트로 참조 |
| `settings.json` | 복사 후 자동 동기화 |
| MCP 서버 | 생성 시점에 복사 (이후 자동 동기화 없음) |

설정은 `~/.claude/settings.json`에서만 고치세요. 다른 계정에서 고치면 다음 갱신 때
덮어써집니다.

### 계정별 명령

계정 수만큼 `claude1.cmd`, `claude2.cmd` … 가 Claude Code 실행 파일 옆에 만들어집니다.
그 폴더는 이미 PATH에 있으므로 PowerShell, cmd, Git Bash 어디서나 씁니다.

```powershell
claude2                 # 2번 계정으로 실행
claude3 --resume        # 인자는 그대로 전달됩니다
```

번호는 패널에 표시된 순서와 같고, 계정 이름 앞의 초록색 숫자로 확인할 수 있습니다.

## 화면 읽는 법

- 원형 게이지는 사용률입니다. 초록 0–49, 노랑 50–74, 주황 75–89, 빨강 90–100.
- `5H`는 5시간 창, `7D`는 주간 창의 **남은 시간**입니다. 주간은 `일:시:분`입니다.
- `Not started`는 그 창이 아직 열리지 않았다는 뜻입니다. 아무것도 쓰지 않은 상태입니다.
- `Reconnect`는 로그인이 필요하다는 뜻입니다. 누르면 그 계정의 터미널이 열립니다.
- 세션 목록의 초록 점은 최근 2분 안에 작업한 세션입니다. 점이 없으면 입력 대기 중입니다.

## 조회 주기에 대해

사용량 엔드포인트는 계정당 허용량이 매우 작습니다. 자주 조회하면 한 시간씩 차단됩니다.
기본값은 사용 중인 계정 3분, 나머지 계정은 그 6배입니다. 차단되면 서버가 알려준 시각까지
기다리고 마지막으로 받은 수치를 그대로 보여줍니다. 그 상태는 디스크에 저장되어 재시작해도
유지됩니다.

여러 개를 띄우면 허용량을 배로 쓰게 되므로 중복 실행은 막혀 있습니다.

## 저장 위치

```
%LOCALAPPDATA%\ECS\ECS.exe       실행 파일 (install.ps1 사용 시)
%APPDATA%\ECS\                   설정, 사용량 캐시, 창 위치, 계정 순서
~/.claude-accounts/<이름>/        계정 프로필
~/.local/bin/claudeN.cmd         계정별 실행 명령
```

자격증명은 읽기만 하며 어디에도 복사하지 않습니다. 캐시에는 사용률 숫자만 들어갑니다.

## 만들어진 방식

.NET 8 / WPF 단일 프로세스입니다. 메모리는 전용 기준 약 60 MB, CPU는 1코어 기준 0.5%
수준입니다.

## 라이선스

MIT
