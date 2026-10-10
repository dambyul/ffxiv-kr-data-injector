# 한국어 이미지 로더

`dinput8.dll`은 `ffxiv-resource-swap/native/loader.cpp` 및 MinHook 1.3.4를 `native/build.cmd`로 빌드한 Windows x64 프록시입니다. Core 어셈블리에 포함하므로 단일 실행 파일 배포에서도 함께 설치됩니다. 리소스 ZIP에서 실행 파일을 다운로드하지 않습니다.

이번 빌드 SHA-256: `68b9a71ccaa91bb5c9137b6e3235e60b5b256ba0dfd917130c3beb2fc69b5dd6`.

소스를 수정하면 native 테스트를 통과한 DLL을 이 폴더에 복사한 후 injector를 다시 빌드하세요. 숫자 6자리 아이콘 경로의 일반·고해상도 TEX를 허용하며, 실제 교체 범위와 제외 번호는 extractor가 생성한 manifest에서 결정합니다. 패키지와 글로벌 실행 파일의 해시를 확인합니다. 게임 패치 후에는 새 글로벌 버전에 맞춰 패키지를 추출해야 합니다.
