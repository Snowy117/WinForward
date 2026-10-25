{
  description = "WinForward 内核化 —— WFP callout 驱动与 C++ 用户态的宿主开发环境";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      llvm = pkgs: pkgs.llvmPackages_21;
    in
    {
      devShells = nixpkgs.lib.genAttrs systems (system:
        let
          pkgs = import nixpkgs { inherit system; };
          llvmPkgs = llvm pkgs;

          # clang-tidy 出自 clang-tools，不是被 cc-wrapper 包装过的驱动，它不知道 libstdc++
          # 的头在哪儿。这里把 GCC 的 C++ 头搜索路径补进 --extra-arg-before。
          clang-tidy = pkgs.writeShellScriptBin "clang-tidy" ''
            extra_args=()
            for include_dir in \
              ${pkgs.gcc.cc}/include/c++/${pkgs.gcc.version} \
              ${pkgs.gcc.cc}/include/c++/${pkgs.gcc.version}/${pkgs.stdenv.hostPlatform.config}
            do
              if [ -d "$include_dir" ]; then
                extra_args+=(--extra-arg-before=-isystem "--extra-arg-before=$include_dir")
              fi
            done
            exec ${llvmPkgs.clang-tools}/bin/clang-tidy "''${extra_args[@]}" "$@"
          '';
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              cmake
              ninja
              llvmPkgs.clang
              gtest

              llvmPkgs.clang-tools

              python3
              # 死符号检查用 cppcheck 的 unusedFunction，不写自研检查器。
              cppcheck
              git
              ccache
              # C# 侧（src/userland）的 blob 编译器与差分测试。目标框架固定 net10.0；
              # Windows VM 上是 SDK 11，也能编 net10。
              dotnet-sdk_10
            ];

            env = {
              CMAKE_EXPORT_COMPILE_COMMANDS = "1";

              # dotnet 首次运行会打印欢迎信息并尝试上报遥测；devShell 里一律关掉。
              DOTNET_CLI_TELEMETRY_OPTOUT = "1";
              DOTNET_NOLOGO = "1";
            };

            shellHook = ''
              # 包装过的 clang-tidy 必须排在 clang-tools 自带的那份前面。
              export PATH="${clang-tidy}/bin:$PATH"

              # mkShell 的 stdenv 会把 CC/CXX 覆盖成 gcc/g++，必须在它之后再导出，
              # 否则 cmake 用 g++ 编译、clang-format/tidy 用 clang 检查，两边悄悄不一致。
              export CC="${llvmPkgs.clang}/bin/clang"
              export CXX="${llvmPkgs.clang}/bin/clang++"

              echo "WinForward 内核化 · 宿主开发环境（$(clang++ --version | head -1)）"
              echo "  驱动与用户态内核侧在 Windows VM 上用 WDK 构建；这里做编辑、格式化与纯逻辑单测。"
            '';
          };
        });
    };
}
