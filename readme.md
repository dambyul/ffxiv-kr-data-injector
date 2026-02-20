# FFXIV KR Data Injector

파이널판타지14의 SqPack 저장소에 사용자 정의 CSV 데이터와 폰트를 주입하는 도구입니다. [FFXIV KR Data Extractor](https://github.com/dambyul/ffxiv-kr-data-extractor)를 통해 생성된 리소스 파일을 게임 클라이언트에 적용합니다.

> [!CAUTION]
> - 본 프로그램의 사용으로 인해 발생하는 모든 문제에 대한 책임은 **사용자 본인**에게 있습니다.

## 주요 기능

- **동적 해시 매핑**: 파일 해시를 로컬 저장소에 동적으로 매핑하여 패치 업데이트 시 하드코딩 수정이 필요하지 않습니다.
- **EXD 데이터 병합**: 원본 EXD 데이터를 보존하며 CSV의 변경 사항만 선택적으로 주입합니다.
- **텍스처 패키징**: 128바이트 블록 정렬 및 표준 비압축 방식을 사용하여 폰트와 텍스처를 패키징합니다.
- **RSV 검증 알림**: 주입 데이터에 미해석된 암호값(RSV)이 포함된 경우 UI에 경고를 표시합니다.
- **설정 외부화**: S3/CloudFront 경로 등의 환경 설정을 `Secrets.cs` 파일로 분리하여 관리합니다.
- **주입 필터링**: 외부 프로그램과의 충돌 방지를 위해 특정 파일 및 행(Row)을 주입 대상에서 제외하는 기능을 제공합니다.

## 프로젝트 구조

```text
.
├── FFXIVInjector.Core/     # SqPack 쓰기, 병합 및 해싱 로직
├── FFXIVInjector.UI/       # Avalonia 기반 GUI 프로젝트
│   ├── Assets/             # 리소스 및 다국어 텍스트 (ko, en)
│   ├── Services/           # 설정 로드 및 로컬라이제이션 서비스
│   ├── ViewModels/         # 비즈니스 로직 (MVVM 패턴)
│   └── Views/              # UI 레이아웃 및 윈도우 정의
├── .gitignore              # Git 추적 제외 설정
└── build.ps1               # 빌드 및 단일 실행 파일 배포 스크립트
```

## 실행 방법

### 요구 사항
- **OS**: Windows 10 이상
- **대상**: FFXIV 글로벌 클라이언트
- **빌드 환경**: .NET 10.0 SDK (소스코드 직접 빌드 시 필요)

### 환경 설정 (빌드하는 경우)
- `FFXIVInjector.Core/Secrets.Example.cs` 파일을 복사하여 `FFXIVInjector.Core/Secrets.cs`를 생성합니다.
- `Secrets.cs` 내부의 리소스 경로를 실제 사용 환경에 맞춰 수정합니다.

### 사용 절차
- **리소스 갱신**: 프로그램 실행 시 설정된 URL에서 최신 리소스(CSV, Font) 버전을 확인하고 다운로드합니다.
- **적용 범위 선택**: UI에서 주입할 대상 프리셋을 선택합니다. 미해석 RSV 데이터가 포함된 항목은 붉은색 아이콘으로 표기됩니다.
- **패치 적용**: 패치 적용 버튼을 클릭하면 원본 데이터 백업을 수행한 후 변경 사항을 클라이언트에 주입합니다.
- **데이터 복원**: 문제 발생 시 복원 버튼을 사용하여 백업된 원본 상태로 클라이언트 데이터를 롤백합니다.

## 참고 자료

- [Lumina](https://github.com/NotAdam/Lumina): 게임 데이터 읽기 및 EXD 구조 해석 엔진
- [FFXIVChnTextPatch-Souma](https://github.com/Souma-Sumire/FFXIVChnTextPatch-Souma): SqPack 주입 및 폰트 패치 구조 참조
- [FFXIVChnTextPatch](https://github.com/reusu/FFXIVChnTextPatch): 텍스트 패치 라이브러리 구현체
- [FFXIV KR Data Extractor](https://github.com/dambyul/ffxiv-kr-data-extractor): 리소스 데이터 생성 및 배포 파이프라인