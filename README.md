[English](README.en.md) | [한국어](README.md)

# 🐾 NyaaChat Client

Windows 데스크톱용 실시간 채팅 클라이언트입니다. .NET Framework 4.8 기반으로 별도의 외부 DLL 없이 단일 실행 파일로 작동하며, 다중 서버 동시 접속을 지원합니다.

- 🌐 **백엔드 채팅 서버 (Node.js)**: [nyaa-chat-server 저장소 바로가기](https://github.com/nemunemulo/nyaa-chat-server)

---

## AI를 활용하여 마음껏 뜯어고치세요

NyaaChat 클라이언트는 누구나 쉽게 개조할 수 있도록 가볍고 단순하게 설계되었습니다.
복잡한 환경 설정 없이 소스 코드와 build.bat만으로 즉시 빌드할 수 있습니다.

AI에게 코드를 전달하고 원하는 UI 디자인, 맞춤형 알림, 자동 응답 매크로 등 필요한 기능을 자유롭게 요청해 보세요.
수정 후 build.bat을 실행하면 나만의 클라이언트가 완성됩니다.
필요에 맞게 자유롭게 뜯어고치고 발전시켜 보세요.

---

## 📁 폴더 및 파일 구조

```text
client/
├── NyaaChat.exe             # 클라이언트 실행 파일 (.NET Framework 4.8)
├── build.bat                # Windows 내장 csc.exe 컴파일 배치 스크립트
├── settings.example.ini     # 설정 예시 파일 (실행 시 settings.ini 자동 생성)
├── aliases.txt              # 슬래시 명령어 단축키 (/j, /w 등)
├── scripts/
│   └── user_script.txt      # 이벤트 트리거 스크립트 예시
├── modules/
│   ├── README.txt           # 서버별 확장 모듈 규격 안내
│   └── sample_CCC.txt       # 서버 전용 모듈 예시
├── themes/
│   ├── default_dark.ini     # 기본 다크 테마
│   ├── classic_white.ini    # 클래식 화이트 테마
│   ├── pc_hitel.ini         # 블루 테마
│   └── matrix_green.ini     # 그린 테마
├── sounds/
│   └── README.txt           # 사용자 효과음 연결 안내문
├── linux_arm/
│   └── nyaachat_native.py   # Linux / ARM 콘솔 클라이언트
└── android_apk/
    └── README.txt           # 모바일 접속 안내
```

---

## 🚀 빌드 방법

Windows 내장 C# 컴파일러(`csc.exe`)를 통해 바로 빌드할 수 있습니다:
```cmd
build.bat
```
빌드가 완료되면 `NyaaChat.exe`가 생성됩니다.

---

## ⚙️ 기본 설정 (`settings.ini`)

프로그램 첫 실행 시 `settings.example.ini`를 바탕으로 `settings.ini`가 자동 생성됩니다.

```ini
[Server]
Url=https://nemulo.duckdns.org
DefaultChannel=#자유대화
AutoConnect=true
AutoConnectServers=https://nemulo.duckdns.org

[AutoJoin]
nemulo.duckdns.org=#자유대화

[User]
DefaultNickname=
NickPassword=
Avatar=🐾
```

- **`Url`**: 기본 접속 서버 주소입니다.
- **`DefaultChannel`**: 첫 접속 시 입장할 기본 채널입니다.
- **`AutoConnectServers`**: 프로그램 시작 시 동시에 연결할 서버 주소 목록입니다 (쉼표로 구분).
- **`[AutoJoin]`**: 서버별 자동 입장 채널 목록을 설정합니다.

---

## 🌐 주요 기능 및 단축키

- **단축키**:
  - `F2`: 화이트리스트 서버 및 공개 채널 목록 창
  - `F10`: 환경설정 창 (서버, 닉네임, 테마, 효과음, 패널 너비 등)
  - `F9` 또는 `/modules`: 서버별 확장 모듈 관리자
  - `Alt + R`: 사용자 정의 스크립트 편집기
  - `Alt + Q`: 창 즉시 숨기기 / 복원 (보스 키)
- **다중 서버 접속**:
  - 좌측 트리에 접속한 서버들이 표시되며, 클릭하여 서버 간을 전환할 수 있습니다.
  - `/server <주소> [#채널]` 명령어로 새로운 서버에 동시 접속할 수 있습니다.

---

## 💬 주요 슬래시 명령어

| 명령어 | 설명 | 예시 |
| :--- | :--- | :--- |
| `/help` | 명령어 도움말 확인 | `/help` |
| `/nick <새닉네임>` | 닉네임 변경 | `/nick 냥이` |
| `/join <채널명> [암호]` | 채널 입장 (신설 또는 입장) | `/join #자유대화` |
| `/part` (또는 `/leave`) | 현재 채널에서 퇴장 | `/part` |
| `/list` | 채널 목록 창 열기 | `/list` |
| `/servers` (또는 `F2`) | 서버 목록 창 열기 | `/servers` |
| `/server <주소> [#채널]` | 다른 서버 동시 접속 창 열기 | `/server https://c.org #게임채널` |
| `/whois <닉네임>` | 사용자 정보 확인 | `/whois 철수` |
| `/msg <닉네임> <내용>` | 1:1 귓속말 전송 | `/msg 영희 안녕!` |
| `/me <행동>` | 3인칭 행동 묘사 메시지 | `/me 기지개를 켠다` |
| `/ping` | 서버 지연시간(RTT) 확인 | `/ping` |
| `/clear` | 대화창 비우기 | `/clear` |

---

## 📜 라이선스

이 프로젝트는 [MIT License](LICENSE)에 따라 자유롭게 사용, 수정, 배포할 수 있습니다.
