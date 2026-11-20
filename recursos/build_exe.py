# =============================================================================
#  Multi Claude — script de build do executável
#  Copyright (C) 2026 Richard KPL — https://github.com/RichardKPL
#  Licensed under the GNU GPL v3 with additional terms (see LICENSE and NOTICE).
# =============================================================================
"""Gera a aplicação Windows e os avisos de licença em dist/."""
import hashlib
from importlib.metadata import distribution
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


RAIZ = Path(__file__).resolve().parents[1]
EXE = "Multi Claude.exe"
AVISOS_TERCEIROS = "THIRD-PARTY-NOTICES.txt"
MODULOS = ("profile_code.py", "profile_settings.py", "profile_shortcut.py", "profile_text.py",
           "profile_switcher.py", "profile_translations.py", "profile_tray.py", "profile_usage.py",
           "windows_profiles.py", "workspace_usage.py")
# Arquivos de distribuição revisados. A galeria também aceita imagens particulares.
RECURSOS_PADRAO = {
    "recursos/icone.ico": "89353c2486242f0db60b677da61cbd9726b4d3dad7ac84d0b3c412eda6d42552",
    "recursos/icone.png": "8fe5236a50b47973ec5ab1732bae07839aa7307ea3b7d0b352916235aa606530",
    "profile_pictures/astronaut.png": "820d488d602a186b76df145b352b6f1b311728c304e9acd3172700906f67232f",
    "profile_pictures/carp.png": "5e45fe046249470f5c3481efd075d8d8aa7d668227e3dbcb55f6ac94f19776de",
    "profile_pictures/comet.png": "94443721dda3dd6db7fc595b92ac28b914b38e2892324752fa0363043028f38f",
    "profile_pictures/cosmic-cat.png": "936a32f6db4c9dcc9932371eeb08eb0546fb4a91e4ad0c0fb7307e0bf7dffa22",
    "profile_pictures/dragon.png": "2916e747d9b24776774a0e102557e7bb2dc17c4c5e8ee57ce5bd6b0c4bf8f55e",
    "profile_pictures/fox.png": "0dbc333aacc19085f0e1564d9a62fd63793f9e4810f2521e4bcf037f3ffe952c",
    "profile_pictures/ghosty.png": "263db3e311af42c5593c35716a744f7f8712632ea9029c3600f5c67d022f41a4",
    "profile_pictures/owl.png": "2c811b2b4d355798ce5d36f3ed5e8f36bb00600b34d9aed1c9dbffd524450f34",
    "profile_pictures/pot.png": "7eddbe81da7c0faeada32c971c2ced65d89dd30931ac927abd36915c133eba00",
    "profile_pictures/robot.png": "c895f325f5615d4d4cbd0da63ebe4caebb755227f0887e91b7fdd70735d03f7e",
    "LICENSE": "3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986",
    "NOTICE": "d70b0367cf36a59e9495d4b2fb90f76cb937a07ac01754d3ef683c5211fb1529",
    "AUTHORS": "e82a4b7df421ae2aefbd9837157a98a83c1d161f8c92cc3c228a5b7a85945aba",
}


def preparar_recursos(pasta):
    for nome, digest in RECURSOS_PADRAO.items():
        origem = RAIZ / nome
        if origem.resolve(strict=True) != origem or not origem.is_file():
            raise ValueError(f"Recurso inválido: {nome}")
        dados = origem.read_bytes()
        # Git pode converter finais de linha dos documentos no Windows.
        canonico = dados.replace(b"\r\n", b"\n") if nome in ("LICENSE", "NOTICE", "AUTHORS") else dados
        if hashlib.sha256(canonico).hexdigest() != digest:
            raise ValueError(f"Recurso diferente da versão revisada: {nome}")
        destino = pasta / nome
        destino.parent.mkdir(parents=True, exist_ok=True)
        destino.write_bytes(dados)


def avisos_de_terceiros():
    """Preserva os textos do interpretador e das wheels usados neste build."""
    partes = [
        b"Third-party notices for Multi Claude",
        f"CPython {sys.version.split()[0]} - LICENSE.txt".encode("ascii"),
        (Path(sys.base_prefix) / "LICENSE.txt").read_bytes(),
    ]
    for projeto, nome in (("PyInstaller", "COPYING.txt"), ("Pillow", "LICENSE")):
        pacote = distribution(projeto)
        arquivo = next((p for p in pacote.files or ()
                        if len(p.parts) == 3 and p.parts[0].endswith(".dist-info")
                        and p.parts[1:] == ("licenses", nome)), None)
        if arquivo is None:
            raise FileNotFoundError(f"Licença ausente: {projeto} {nome}")
        partes.append(f"{projeto} {pacote.version} - {arquivo.as_posix()}".encode("utf8"))
        partes.append(arquivo.locate().read_bytes())
    return b"\n\n".join(partes) + b"\n"


def arquivo_versao(pasta, v):
    n = tuple(int(x) for x in (v.split(".") + ["0"] * 4)[:4])
    caminho = pasta / "versao.txt"
    caminho.write_text(f"""VSVersionInfo(
  ffi=FixedFileInfo(filevers={n}, prodvers={n}, mask=0x3f, flags=0x0, OS=0x40004,
                    fileType=0x1, subtype=0x0, date=(0, 0)),
  kids=[
    StringFileInfo([StringTable('041604B0', [
      StringStruct('FileDescription', 'Multi Claude Codex — modified edition by Axel (AFielen)'),
      StringStruct('FileVersion', '{v}'),
      StringStruct('InternalName', 'Multi Claude'),
      StringStruct('LegalCopyright', 'Based on Multi Claude, created by Richard KPL (https://github.com/RichardKPL) — GPL-3.0'),
      StringStruct('OriginalFilename', 'Multi Claude.exe'),
      StringStruct('ProductName', 'Multi Claude Codex'),
      StringStruct('ProductVersion', '{v}')])]),
    VarFileInfo([VarStruct('Translation', [0x0416, 1200])])
  ]
)
""", encoding="utf8")
    return caminho


def main():
    dist = RAIZ / "dist"
    if dist.resolve() != dist:
        raise ValueError(f"Diretório de destino redirecionado: {dist}")
    dist.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".multi-claude-", dir=dist) as local:
        pasta = Path(local)
        preparar_recursos(pasta)
        (pasta / AVISOS_TERCEIROS).write_bytes(avisos_de_terceiros())
        fonte = pasta / "Multi Claude.pyw"
        shutil.copyfile(RAIZ / fonte.name, fonte)
        for nome in MODULOS:
            shutil.copyfile(RAIZ / nome, pasta / nome)
        v = re.search(r'^VERSAO = "([\d.]+)"', fonte.read_text(encoding="utf8"), re.M).group(1)
        args = [
            sys.executable, "-I", "-m", "PyInstaller", str(fonte),
            "--onefile", "--windowed", "--noconfirm", "--noupx",
            "--name", "Multi Claude",
            "--icon", str(pasta / "recursos/icone.ico"),
            "--version-file", str(arquivo_versao(pasta, v)),
            "--copy-metadata", "Pillow",
            "--distpath", str(pasta / "out"),
            "--workpath", str(pasta / "work"),
            "--specpath", str(pasta),
        ]
        for nome in RECURSOS_PADRAO:
            args.extend(["--add-data", f"{pasta / nome}:{Path(nome).parent}"])
        env = {**os.environ, "PYINSTALLER_CONFIG_DIR": str(pasta / "cache")}
        subprocess.run(args, cwd=pasta, env=env, check=True)
        for nome in ("LICENSE", "NOTICE", "AUTHORS", AVISOS_TERCEIROS):
            (pasta / nome).replace(dist / nome)
        (pasta / "out" / EXE).replace(dist / EXE)
    print(f"Pronto: {dist / EXE} (v{v})")


if __name__ == "__main__":
    if sys.platform != "win32":
        raise SystemExit("A compilação do executável requer Windows.")
    main()
