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
              git
              ccache
            ];

            env = {
              CC = "${llvmPkgs.clang}/bin/clang";
              CXX = "${llvmPkgs.clang}/bin/clang++";
              CMAKE_EXPORT_COMPILE_COMMANDS = "1";
            };

            shellHook = ''
              # 包装过的 clang-tidy 必须排在 clang-tools 自带的那份前面。
              export PATH="${clang-tidy}/bin:$PATH"

              echo "WinForward 内核化 · 宿主开发环境（$(clang++ --version | head -1)）"
              echo "  驱动与用户态内核侧在 Windows VM 上用 WDK 构建；这里做编辑、格式化与纯逻辑单测。"
            '';
          };
        });
    };
}
