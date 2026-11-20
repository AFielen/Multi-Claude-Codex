# =============================================================================
#  Multi Claude — https://github.com/RichardKPL/multi-claude
#  Copyright (C) 2026 Richard KPL — https://github.com/RichardKPL
#
#  This program is free software: you can redistribute it and/or modify it
#  under the terms of the GNU General Public License version 3 as published by
#  the Free Software Foundation, with the additional terms of section 7 listed
#  in the NOTICE file (preservation of the original author attribution).
#
#  This program is distributed in the hope that it will be useful, but WITHOUT
#  ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
#  FITNESS FOR A PARTICULAR PURPOSE. See the LICENSE file for details.
#
#  Multi Claude is an independent project, not affiliated with Anthropic.
# =============================================================================
"""Multi Claude - abre várias instâncias do Claude Desktop, cada uma com sua conta.

Cada perfil usa uma pasta de dados própria (--user-data-dir), então o login
de um não interfere no outro. Os perfis ficam salvos em perfis.db (SQLite) e
as fotos de perfil na pasta profile_pictures (qualquer imagem lá vira opção).

Login: o usuário pode colar o link do navegador no diálogo do perfil escolhido.
"""
import contextlib
import ctypes
import ctypes.wintypes as wt
import hashlib
import json
import os
import re
import shutil
import sqlite3
import stat
import subprocess
import sys
import tempfile
import time
import tkinter as tk
import webbrowser
import winreg
from pathlib import Path
from tkinter import filedialog

try:
    from PIL import Image, ImageDraw, ImageTk
except ImportError:  # sem Pillow: avatares só com a inicial
    Image = None

APP = "Multi Claude"
VERSAO = "2.1.0"
AUTOR = "Richard KPL"
GITHUB = "https://github.com/RichardKPL"
REPO = "https://github.com/RichardKPL/multi-claude"
COPYRIGHT = f"© 2026 {AUTOR}"
ATRIBUICAO = "Based on Multi Claude, created by Richard KPL"

CONGELADO = getattr(sys, "frozen", False)
PASTA_APP = os.path.dirname(sys.executable if CONGELADO else os.path.abspath(__file__))
RECURSOS = getattr(sys, "_MEIPASS", PASTA_APP)  # dentro do .exe, os arquivos embutidos ficam aqui
# Portátil: tudo fica ao lado do programa (MULTI_CLAUDE_HOME troca o local, útil para testes).
BASE = os.path.abspath(os.environ.get("MULTI_CLAUDE_HOME") or PASTA_APP)
DB = os.path.join(BASE, "perfis.db")
LOG = os.path.join(BASE, "log.txt")
FOTOS = os.path.join(BASE, "profile_pictures")
DADOS = os.path.join(BASE, "dados")  # pastas de dados dos Claude de cada perfil
ICONE = os.path.join(RECURSOS, "recursos", "icone.ico")
ICONE_PNG = os.path.join(RECURSOS, "recursos", "icone.png")
EXT_IMAGEM = (".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff")
LOGIN_TIMEOUT = 300  # segundos esperando o login
FONTE = "Segoe UI"

# ---------- temas ----------

TEMAS = {
    "escuro": dict(BG="#1a1918", PAINEL="#232220", CARD="#262523", HOVER="#2f2e2b", BORDA="#36342f",
                   TEXTO="#f5f4ef", SUB="#a39f95", BTN="#33312d", BTN_HOVER="#3f3c37",
                   CHIP="#2f2d2a", MENU="#2a2926"),
    "claro": dict(BG="#f4f2ec", PAINEL="#ffffff", CARD="#ffffff", HOVER="#faf8f3", BORDA="#e2ded5",
                  TEXTO="#1f1e1d", SUB="#6d6a62", BTN="#ece8df", BTN_HOVER="#e1dcd1",
                  CHIP="#f1eee7", MENU="#ffffff"),
}
FIXAS = dict(ACCENT="#d97757", ACCENT_HOVER="#e4876a", VERDE="#5fa85e", AMARELO="#d39b33", VERMELHO="#d9534f")
AVATARES = ["#d97757", "#6a9bd8", "#5fa85e", "#b77fd0", "#d39b33", "#4fb0a8", "#d86a8f"]
C = {}  # paleta ativa


def idioma_do_windows():
    """Idioma sugerido na primeira abertura, a partir do idioma do Windows."""
    try:
        primario = ctypes.windll.kernel32.GetUserDefaultUILanguage() & 0x3FF
    except Exception:
        return "en"
    return {0x16: "pt", 0x09: "en", 0x0A: "es", 0x0C: "fr", 0x07: "de", 0x10: "it",
            0x19: "ru", 0x11: "ja", 0x04: "zh", 0x12: "ko"}.get(primario, "en")


def tema_sistema():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as k:
            return "claro" if winreg.QueryValueEx(k, "AppsUseLightTheme")[0] else "escuro"
    except OSError:
        return "escuro"


def aplicar_tema(escolha):
    efetivo = tema_sistema() if escolha == "sistema" else escolha
    C.clear()
    C.update(TEMAS[efetivo], **FIXAS)
    C["nome"] = efetivo
    return efetivo


def barra_titulo(janela):
    """Deixa a barra de título do Windows no mesmo tema (escuro/claro) da janela."""
    try:
        janela.update_idletasks()
        hwnd = ctypes.windll.user32.GetParent(janela.winfo_id())
        valor = ctypes.c_int(1 if C.get("nome") == "escuro" else 0)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(valor), ctypes.sizeof(valor))
    except Exception:
        pass


def por_icone(janela):
    try:
        janela.iconbitmap(ICONE)
        return
    except tk.TclError:
        pass
    # .ico inválido (ex.: PNG renomeado): usa o PNG direto.
    for arq in (ICONE, ICONE_PNG):
        try:
            img = ImageTk.PhotoImage(Image.open(arq).convert("RGBA").resize((64, 64), Image.LANCZOS))
            janela.iconphoto(False, img)
            janela._icone = img
            return
        except Exception:
            continue


# ---------- idiomas ----------

IDIOMAS = [("pt", "Português (BR)"), ("en", "English"), ("es", "Español"), ("fr", "Français"),
           ("de", "Deutsch"), ("it", "Italiano"), ("ru", "Русский"), ("ja", "日本語"),
           ("zh", "中文"), ("ko", "한국어")]

T = {
    "pt": dict(
        sub="Várias contas do Claude Desktop, cada uma na sua janela.",
        ausente="Claude Desktop não encontrado. Instale-o primeiro.",
        novo="Novo perfil", nenhum="Nenhum perfil ainda", nenhum_sub="Crie um perfil para cada conta do Claude.",
        conectado="Conectado", desconectado="Sem login", aberto="Aberto", fechado="Fechado",
        aguard="Aguardando login…", abrir="Abrir", fechar="Fechar", entrar="Entrar", sair="Sair da conta",
        renomear="Renomear", pasta="Pasta de dados", excluir="Excluir", erro="Erro", foto="Foto de perfil",
        aguardando="Inicie sessão no Claude para “{n}”. Se o navegador oferecer um link de retorno, copie-o e use “Colar link” aqui.",
        colar="Colar link", cancelar="Cancelar", colar_pergunta="Cole o link claude:// do navegador:",
        invalido="Cole o link de retorno completo fornecido pelo Claude para este perfil.",
        enviado="✓ Login enviado para “{n}”.", esgotado="Tempo de login esgotado. Tente “Entrar” de novo.",
        novo_pergunta="Nome do perfil (ex.: Conta Trabalho):", existe="Já existe um perfil com esse nome.",
        renomear_pergunta="Novo nome:", excluir_q="Excluir o perfil “{n}”?",
        excluir_dados="Apagar também os dados desse perfil (login, sessões)?",
        sair_q="Sair da conta no perfil “{n}”?\nO Claude desse perfil será fechado.",
        saiu="Você saiu da conta em “{n}”.", idioma="Idioma", config="Configurações",
        aparencia="Aparência", claro="Claro", escuro="Escuro", sistema="Sistema", sobre="Sobre",
        versao="Versão", direitos="Todos os direitos reservados.", fotos_titulo="Foto de “{n}”",
        adicionar_img="Adicionar imagem…", abrir_fotos="Abrir pasta", sem_foto="Sem foto",
        fotos_dica="Qualquer imagem colocada na pasta profile_pictures aparece aqui.",
        imagens="Imagens", ok="OK", sim="Sim", nao="Não", img_erro="Não foi possível abrir essa imagem.",
        padrao=["Conta 1", "Conta 2"]),
    "en": dict(
        sub="Several Claude Desktop accounts, each in its own window.",
        ausente="Claude Desktop not found. Please install it first.",
        novo="New profile", nenhum="No profiles yet", nenhum_sub="Create one profile for each Claude account.",
        conectado="Signed in", desconectado="Not signed in", aberto="Open", fechado="Closed",
        aguard="Waiting for sign-in…", abrir="Open", fechar="Close", entrar="Sign in", sair="Sign out",
        renomear="Rename", pasta="Data folder", excluir="Delete", erro="Error", foto="Profile picture",
        aguardando="Sign in to Claude for “{n}”. If your browser offers a return link, copy it and use “Paste link” here.",
        colar="Paste link", cancelar="Cancel", colar_pergunta="Paste the claude:// link from the browser:",
        invalido="Paste the complete sign-in return link provided by Claude for this profile.",
        enviado="✓ Sign-in sent to “{n}”.", esgotado="Sign-in timed out. Try “Sign in” again.",
        novo_pergunta="Profile name (e.g. Work account):", existe="A profile with that name already exists.",
        renomear_pergunta="New name:", excluir_q="Delete profile “{n}”?",
        excluir_dados="Also erase this profile's data (sign-in, sessions)?",
        sair_q="Sign out of profile “{n}”?\nThis profile's Claude will be closed.",
        saiu="You signed out of “{n}”.", idioma="Language", config="Settings",
        aparencia="Appearance", claro="Light", escuro="Dark", sistema="System", sobre="About",
        versao="Version", direitos="All rights reserved.", fotos_titulo="Picture for “{n}”",
        adicionar_img="Add image…", abrir_fotos="Open folder", sem_foto="No picture",
        fotos_dica="Any image placed in the profile_pictures folder shows up here.",
        imagens="Images", ok="OK", sim="Yes", nao="No", img_erro="Could not open that image.",
        padrao=["Account 1", "Account 2"]),
    "es": dict(
        sub="Varias cuentas de Claude Desktop, cada una en su ventana.",
        ausente="Claude Desktop no encontrado. Instálalo primero.",
        novo="Nuevo perfil", nenhum="Aún no hay perfiles", nenhum_sub="Crea un perfil para cada cuenta de Claude.",
        conectado="Conectado", desconectado="Sin sesión", aberto="Abierto", fechado="Cerrado",
        aguard="Esperando inicio de sesión…", abrir="Abrir", fechar="Cerrar", entrar="Iniciar sesión",
        sair="Cerrar sesión", renomear="Renombrar", pasta="Carpeta de datos", excluir="Eliminar", erro="Error",
        foto="Foto de perfil",
        aguardando="Inicia sesión en Claude para «{n}». Si el navegador ofrece un enlace de retorno, cópialo y usa «Pegar enlace» aquí.",
        colar="Pegar enlace", cancelar="Cancelar", colar_pergunta="Pega el enlace claude:// del navegador:",
        invalido="Pega el enlace de retorno completo que Claude proporciona para este perfil.",
        enviado="✓ Sesión enviada a “{n}”.", esgotado="Se agotó el tiempo. Prueba “Iniciar sesión” de nuevo.",
        novo_pergunta="Nombre del perfil (ej.: Cuenta Trabajo):", existe="Ya existe un perfil con ese nombre.",
        renomear_pergunta="Nuevo nombre:", excluir_q="¿Eliminar el perfil “{n}”?",
        excluir_dados="¿Borrar también los datos de este perfil (sesión, historial)?",
        sair_q="¿Cerrar sesión en el perfil “{n}”?\nEl Claude de este perfil se cerrará.",
        saiu="Cerraste sesión en “{n}”.", idioma="Idioma", config="Configuración",
        aparencia="Apariencia", claro="Claro", escuro="Oscuro", sistema="Sistema", sobre="Acerca de",
        versao="Versión", direitos="Todos los derechos reservados.", fotos_titulo="Foto de “{n}”",
        adicionar_img="Añadir imagen…", abrir_fotos="Abrir carpeta", sem_foto="Sin foto",
        fotos_dica="Cualquier imagen en la carpeta profile_pictures aparece aquí.",
        imagens="Imágenes", ok="OK", sim="Sí", nao="No", img_erro="No se pudo abrir esa imagen.",
        padrao=["Cuenta 1", "Cuenta 2"]),
    "fr": dict(
        sub="Plusieurs comptes Claude Desktop, chacun dans sa fenêtre.",
        ausente="Claude Desktop introuvable. Installez-le d'abord.",
        novo="Nouveau profil", nenhum="Aucun profil", nenhum_sub="Créez un profil pour chaque compte Claude.",
        conectado="Connecté", desconectado="Non connecté", aberto="Ouvert", fechado="Fermé",
        aguard="En attente de connexion…", abrir="Ouvrir", fechar="Fermer", entrar="Se connecter",
        sair="Se déconnecter", renomear="Renommer", pasta="Dossier de données", excluir="Supprimer",
        erro="Erreur", foto="Photo de profil",
        aguardando="Connectez-vous à Claude pour « {n} ». Si le navigateur propose un lien de retour, copiez-le et cliquez ici sur « Coller le lien ».",
        colar="Coller le lien", cancelar="Annuler", colar_pergunta="Collez le lien claude:// du navigateur :",
        invalido="Collez le lien de retour complet fourni par Claude pour ce profil.",
        enviado="✓ Connexion envoyée à « {n} ».", esgotado="Délai dépassé. Réessayez « Se connecter ».",
        novo_pergunta="Nom du profil (ex. : Compte travail) :", existe="Un profil porte déjà ce nom.",
        renomear_pergunta="Nouveau nom :", excluir_q="Supprimer le profil « {n} » ?",
        excluir_dados="Effacer aussi les données de ce profil (connexion, sessions) ?",
        sair_q="Se déconnecter du profil « {n} » ?\nLe Claude de ce profil sera fermé.",
        saiu="Vous êtes déconnecté de « {n} ».", idioma="Langue", config="Paramètres",
        aparencia="Apparence", claro="Clair", escuro="Sombre", sistema="Système", sobre="À propos",
        versao="Version", direitos="Tous droits réservés.", fotos_titulo="Photo de « {n} »",
        adicionar_img="Ajouter une image…", abrir_fotos="Ouvrir le dossier", sem_foto="Sans photo",
        fotos_dica="Toute image placée dans le dossier profile_pictures apparaît ici.",
        imagens="Images", ok="OK", sim="Oui", nao="Non", img_erro="Impossible d'ouvrir cette image.",
        padrao=["Compte 1", "Compte 2"]),
    "de": dict(
        sub="Mehrere Claude-Desktop-Konten, jedes im eigenen Fenster.",
        ausente="Claude Desktop nicht gefunden. Bitte zuerst installieren.",
        novo="Neues Profil", nenhum="Noch keine Profile", nenhum_sub="Lege für jedes Claude-Konto ein Profil an.",
        conectado="Angemeldet", desconectado="Nicht angemeldet", aberto="Geöffnet", fechado="Geschlossen",
        aguard="Warte auf Anmeldung…", abrir="Öffnen", fechar="Schließen", entrar="Anmelden", sair="Abmelden",
        renomear="Umbenennen", pasta="Datenordner", excluir="Löschen", erro="Fehler", foto="Profilbild",
        aguardando="Melde dich für „{n}“ in Claude an. Wenn der Browser einen Rückkehrlink anbietet, kopiere ihn und wähle hier „Link einfügen“.",
        colar="Link einfügen", cancelar="Abbrechen", colar_pergunta="Füge den claude://-Link aus dem Browser ein:",
        invalido="Füge den vollständigen Rückkehrlink ein, den Claude für die Anmeldung mit diesem Profil anbietet.",
        enviado="✓ Anmeldung an „{n}“ gesendet.", esgotado="Zeit abgelaufen. Versuche „Anmelden“ erneut.",
        novo_pergunta="Profilname (z. B. Arbeitskonto):", existe="Ein Profil mit diesem Namen existiert bereits.",
        renomear_pergunta="Neuer Name:", excluir_q="Profil „{n}“ löschen?",
        excluir_dados="Auch die Daten dieses Profils (Anmeldung, Sitzungen) löschen?",
        sair_q="Vom Profil „{n}“ abmelden?\nDas Claude dieses Profils wird geschlossen.",
        saiu="Du hast dich von „{n}“ abgemeldet.", idioma="Sprache", config="Einstellungen",
        aparencia="Darstellung", claro="Hell", escuro="Dunkel", sistema="System", sobre="Über",
        versao="Version", direitos="Alle Rechte vorbehalten.", fotos_titulo="Bild für „{n}“",
        adicionar_img="Bild hinzufügen…", abrir_fotos="Ordner öffnen", sem_foto="Kein Bild",
        fotos_dica="Jedes Bild im Ordner profile_pictures erscheint hier.",
        imagens="Bilder", ok="OK", sim="Ja", nao="Nein", img_erro="Dieses Bild konnte nicht geöffnet werden.",
        padrao=["Konto 1", "Konto 2"]),
    "it": dict(
        sub="Più account di Claude Desktop, ognuno nella sua finestra.",
        ausente="Claude Desktop non trovato. Installalo prima.",
        novo="Nuovo profilo", nenhum="Nessun profilo", nenhum_sub="Crea un profilo per ogni account Claude.",
        conectado="Connesso", desconectado="Non connesso", aberto="Aperto", fechado="Chiuso",
        aguard="In attesa di accesso…", abrir="Apri", fechar="Chiudi", entrar="Accedi", sair="Esci dall'account",
        renomear="Rinomina", pasta="Cartella dati", excluir="Elimina", erro="Errore", foto="Foto profilo",
        aguardando="Accedi a Claude per «{n}». Se il browser offre un link di ritorno, copialo e scegli «Incolla link» qui.",
        colar="Incolla link", cancelar="Annulla", colar_pergunta="Incolla il link claude:// dal browser:",
        invalido="Incolla il link di ritorno completo fornito da Claude per questo profilo.",
        enviado="✓ Accesso inviato a “{n}”.", esgotado="Tempo scaduto. Riprova “Accedi”.",
        novo_pergunta="Nome del profilo (es.: Account lavoro):", existe="Esiste già un profilo con questo nome.",
        renomear_pergunta="Nuovo nome:", excluir_q="Eliminare il profilo “{n}”?",
        excluir_dados="Cancellare anche i dati di questo profilo (accesso, sessioni)?",
        sair_q="Uscire dall'account nel profilo “{n}”?\nIl Claude di questo profilo verrà chiuso.",
        saiu="Sei uscito da “{n}”.", idioma="Lingua", config="Impostazioni",
        aparencia="Aspetto", claro="Chiaro", escuro="Scuro", sistema="Sistema", sobre="Informazioni",
        versao="Versione", direitos="Tutti i diritti riservati.", fotos_titulo="Foto di “{n}”",
        adicionar_img="Aggiungi immagine…", abrir_fotos="Apri cartella", sem_foto="Nessuna foto",
        fotos_dica="Qualsiasi immagine nella cartella profile_pictures appare qui.",
        imagens="Immagini", ok="OK", sim="Sì", nao="No", img_erro="Impossibile aprire questa immagine.",
        padrao=["Account 1", "Account 2"]),
    "ru": dict(
        sub="Несколько аккаунтов Claude Desktop, каждый в своём окне.",
        ausente="Claude Desktop не найден. Сначала установите его.",
        novo="Новый профиль", nenhum="Профилей пока нет", nenhum_sub="Создайте профиль для каждого аккаунта Claude.",
        conectado="Вход выполнен", desconectado="Не выполнен вход", aberto="Открыт", fechado="Закрыт",
        aguard="Ожидание входа…", abrir="Открыть", fechar="Закрыть", entrar="Войти", sair="Выйти",
        renomear="Переименовать", pasta="Папка данных", excluir="Удалить", erro="Ошибка", foto="Фото профиля",
        aguardando="Войдите в Claude для профиля «{n}». Если браузер предложит ссылку для возврата, скопируйте её и нажмите здесь «Вставить ссылку».",
        colar="Вставить ссылку", cancelar="Отмена", colar_pergunta="Вставьте ссылку claude:// из браузера:",
        invalido="Вставьте полную ссылку для возврата в Claude для этого профиля.",
        enviado="✓ Вход отправлен в «{n}».", esgotado="Время вышло. Попробуйте «Войти» снова.",
        novo_pergunta="Имя профиля (напр.: Рабочий):", existe="Профиль с таким именем уже есть.",
        renomear_pergunta="Новое имя:", excluir_q="Удалить профиль «{n}»?",
        excluir_dados="Удалить также данные профиля (вход, сессии)?",
        sair_q="Выйти из аккаунта в профиле «{n}»?\nClaude этого профиля будет закрыт.",
        saiu="Вы вышли из «{n}».", idioma="Язык", config="Настройки",
        aparencia="Оформление", claro="Светлая", escuro="Тёмная", sistema="Системная", sobre="О программе",
        versao="Версия", direitos="Все права защищены.", fotos_titulo="Фото для «{n}»",
        adicionar_img="Добавить изображение…", abrir_fotos="Открыть папку", sem_foto="Без фото",
        fotos_dica="Любое изображение в папке profile_pictures появится здесь.",
        imagens="Изображения", ok="OK", sim="Да", nao="Нет", img_erro="Не удалось открыть изображение.",
        padrao=["Аккаунт 1", "Аккаунт 2"]),
    "ja": dict(
        sub="複数の Claude Desktop アカウントを、それぞれ別のウィンドウで。",
        ausente="Claude Desktop が見つかりません。先にインストールしてください。",
        novo="新しいプロフィール", nenhum="プロフィールがありません", nenhum_sub="Claude アカウントごとにプロフィールを作成します。",
        conectado="ログイン済み", desconectado="未ログイン", aberto="起動中", fechado="終了",
        aguard="ログイン待ち…", abrir="開く", fechar="閉じる", entrar="ログイン", sair="ログアウト",
        renomear="名前を変更", pasta="データフォルダー", excluir="削除", erro="エラー", foto="プロフィール画像",
        aguardando="「{n}」で Claude にログインしてください。ブラウザーに戻るためのリンクが表示されたら、コピーしてここで「リンクを貼り付け」を選びます。",
        colar="リンクを貼り付け", cancelar="キャンセル", colar_pergunta="ブラウザーの claude:// リンクを貼り付けてください：",
        invalido="このプロフィールのログイン時に Claude が表示した戻り先のリンク全体を貼り付けてください。",
        enviado="✓ 「{n}」にログインを送信しました。", esgotado="時間切れです。もう一度「ログイン」してください。",
        novo_pergunta="プロフィール名（例：仕事用）：", existe="同じ名前のプロフィールがあります。",
        renomear_pergunta="新しい名前：", excluir_q="プロフィール「{n}」を削除しますか？",
        excluir_dados="このプロフィールのデータ（ログイン、セッション）も消去しますか？",
        sair_q="プロフィール「{n}」からログアウトしますか？\nこのプロフィールの Claude は終了します。",
        saiu="「{n}」からログアウトしました。", idioma="言語", config="設定",
        aparencia="外観", claro="ライト", escuro="ダーク", sistema="システム", sobre="このアプリについて",
        versao="バージョン", direitos="All rights reserved.", fotos_titulo="「{n}」の画像",
        adicionar_img="画像を追加…", abrir_fotos="フォルダーを開く", sem_foto="画像なし",
        fotos_dica="profile_pictures フォルダーに置いた画像はすべてここに表示されます。",
        imagens="画像", ok="OK", sim="はい", nao="いいえ", img_erro="この画像を開けませんでした。",
        padrao=["アカウント 1", "アカウント 2"]),
    "zh": dict(
        sub="多个 Claude Desktop 账号，各自独立窗口。",
        ausente="未找到 Claude Desktop，请先安装。",
        novo="新建配置", nenhum="还没有配置", nenhum_sub="为每个 Claude 账号创建一个配置。",
        conectado="已登录", desconectado="未登录", aberto="已打开", fechado="已关闭",
        aguard="等待登录…", abrir="打开", fechar="关闭", entrar="登录", sair="退出登录",
        renomear="重命名", pasta="数据文件夹", excluir="删除", erro="错误", foto="头像",
        aguardando="请为“{n}”登录 Claude。如果浏览器提供返回链接，请复制该链接并在此处选择“粘贴链接”。",
        colar="粘贴链接", cancelar="取消", colar_pergunta="粘贴浏览器中的 claude:// 链接：",
        invalido="请粘贴 Claude 为此资料提供的完整登录返回链接。",
        enviado="✓ 已将登录发送到“{n}”。", esgotado="登录超时，请重新点击“登录”。",
        novo_pergunta="配置名称（例如：工作账号）：", existe="已存在同名配置。",
        renomear_pergunta="新名称：", excluir_q="删除配置“{n}”？",
        excluir_dados="同时清除此配置的数据（登录、会话）？",
        sair_q="退出配置“{n}”的账号？\n此配置的 Claude 将被关闭。",
        saiu="已退出“{n}”。", idioma="语言", config="设置",
        aparencia="外观", claro="浅色", escuro="深色", sistema="跟随系统", sobre="关于",
        versao="版本", direitos="保留所有权利。", fotos_titulo="“{n}”的头像",
        adicionar_img="添加图片…", abrir_fotos="打开文件夹", sem_foto="无头像",
        fotos_dica="放入 profile_pictures 文件夹的任何图片都会显示在这里。",
        imagens="图片", ok="确定", sim="是", nao="否", img_erro="无法打开此图片。",
        padrao=["账号 1", "账号 2"]),
    "ko": dict(
        sub="여러 Claude Desktop 계정을 각각 다른 창에서.",
        ausente="Claude Desktop을 찾을 수 없습니다. 먼저 설치하세요.",
        novo="새 프로필", nenhum="프로필이 없습니다", nenhum_sub="Claude 계정마다 프로필을 만드세요.",
        conectado="로그인됨", desconectado="로그인 안 됨", aberto="열림", fechado="닫힘",
        aguard="로그인 대기 중…", abrir="열기", fechar="닫기", entrar="로그인", sair="로그아웃",
        renomear="이름 바꾸기", pasta="데이터 폴더", excluir="삭제", erro="오류", foto="프로필 사진",
        aguardando="“{n}” 프로필로 Claude에 로그인하세요. 브라우저에 돌아가기 링크가 표시되면 복사한 후 여기서 “링크 붙여넣기”를 선택하세요.",
        colar="링크 붙여넣기", cancelar="취소", colar_pergunta="브라우저의 claude:// 링크를 붙여넣으세요:",
        invalido="이 프로필에 대해 Claude에서 제공한 로그인 복귀 링크 전체를 붙여넣으세요.",
        enviado="✓ “{n}”에 로그인을 보냈습니다.", esgotado="시간이 초과되었습니다. 다시 “로그인”하세요.",
        novo_pergunta="프로필 이름 (예: 업무 계정):", existe="같은 이름의 프로필이 이미 있습니다.",
        renomear_pergunta="새 이름:", excluir_q="“{n}” 프로필을 삭제할까요?",
        excluir_dados="이 프로필의 데이터(로그인, 세션)도 지울까요?",
        sair_q="“{n}” 프로필에서 로그아웃할까요?\n이 프로필의 Claude가 닫힙니다.",
        saiu="“{n}”에서 로그아웃했습니다.", idioma="언어", config="설정",
        aparencia="모양", claro="라이트", escuro="다크", sistema="시스템", sobre="정보",
        versao="버전", direitos="All rights reserved.", fotos_titulo="“{n}” 사진",
        adicionar_img="이미지 추가…", abrir_fotos="폴더 열기", sem_foto="사진 없음",
        fotos_dica="profile_pictures 폴더에 넣은 모든 이미지가 여기에 표시됩니다.",
        imagens="이미지", ok="확인", sim="예", nao="아니요", img_erro="이미지를 열 수 없습니다.",
        padrao=["계정 1", "계정 2"]),
}

# Textos da v2.1 (boas-vindas, importação, zona de perigo, licença)
_EXTRA = {
    "pt": dict(
        bv_titulo="Bem-vindo ao Multi Claude", continuar="Continuar",
        bv_sub="Escolha o idioma do programa. Dá para mudar depois em ⚙ Configurações.",
        importar="Importar perfis", importar_sub="Perfis do Claude encontrados nesta máquina que ainda não estão no Multi Claude:",
        nenhum_encontrado="Nenhum perfil novo encontrado. Use “Procurar pasta…” para escolher um manualmente.",
        procurar_pasta="Procurar pasta…", procurar_db="Importar perfis.db…",
        pasta_invalida="Essa pasta não parece ser um perfil do Claude (faltam config.json e Local State).",
        importar_sel="Importar selecionados", importados="{n} perfil(is) importado(s).",
        dados_sec="Dados", perigo="Zona de perigo", apagar_tudo="Apagar todos os dados",
        apagar_tudo_sub="Apaga todos os perfis, logins, sessões, fotos enviadas e configurações do Multi Claude. "
                        "Não dá para desfazer.",
        apagar_confirma="Isso não pode ser desfeito. Para confirmar, digite {p}:", palavra="APAGAR",
        direitos="Software livre sob a licença GPL-3.0.", licenca="Licença", codigo="Código-fonte",
        nao_oficial="Projeto independente, sem ligação com a Anthropic."),
    "en": dict(
        bv_titulo="Welcome to Multi Claude", continuar="Continue",
        bv_sub="Choose the app language. You can change it later in ⚙ Settings.",
        importar="Import profiles", importar_sub="Claude profiles found on this computer that are not in Multi Claude yet:",
        nenhum_encontrado="No new profiles found. Use “Browse folder…” to pick one manually.",
        procurar_pasta="Browse folder…", procurar_db="Import perfis.db…",
        pasta_invalida="That folder doesn't look like a Claude profile (config.json and Local State are missing).",
        importar_sel="Import selected", importados="{n} profile(s) imported.",
        dados_sec="Data", perigo="Danger zone", apagar_tudo="Erase all data",
        apagar_tudo_sub="Erases every profile, sign-in, session, uploaded picture and setting of Multi Claude. "
                        "This cannot be undone.",
        apagar_confirma="This cannot be undone. To confirm, type {p}:", palavra="DELETE",
        direitos="Free software under the GPL-3.0 license.", licenca="License", codigo="Source code",
        nao_oficial="Independent project, not affiliated with Anthropic."),
    "es": dict(
        bv_titulo="Bienvenido a Multi Claude", continuar="Continuar",
        bv_sub="Elige el idioma del programa. Puedes cambiarlo después en ⚙ Configuración.",
        importar="Importar perfiles", importar_sub="Perfiles de Claude encontrados en este equipo que aún no están en Multi Claude:",
        nenhum_encontrado="No se encontraron perfiles nuevos. Usa “Buscar carpeta…” para elegir uno manualmente.",
        procurar_pasta="Buscar carpeta…", procurar_db="Importar perfis.db…",
        pasta_invalida="Esa carpeta no parece un perfil de Claude (faltan config.json y Local State).",
        importar_sel="Importar seleccionados", importados="{n} perfil(es) importado(s).",
        dados_sec="Datos", perigo="Zona de peligro", apagar_tudo="Borrar todos los datos",
        apagar_tudo_sub="Borra todos los perfiles, sesiones, historial, fotos subidas y ajustes de Multi Claude. "
                        "No se puede deshacer.",
        apagar_confirma="No se puede deshacer. Para confirmar, escribe {p}:", palavra="BORRAR",
        direitos="Software libre bajo la licencia GPL-3.0.", licenca="Licencia", codigo="Código fuente",
        nao_oficial="Proyecto independiente, sin relación con Anthropic."),
    "fr": dict(
        bv_titulo="Bienvenue dans Multi Claude", continuar="Continuer",
        bv_sub="Choisissez la langue de l'application. Vous pourrez la changer dans ⚙ Paramètres.",
        importar="Importer des profils", importar_sub="Profils Claude trouvés sur cet ordinateur qui ne sont pas encore dans Multi Claude :",
        nenhum_encontrado="Aucun nouveau profil trouvé. Utilisez « Parcourir… » pour en choisir un.",
        procurar_pasta="Parcourir…", procurar_db="Importer perfis.db…",
        pasta_invalida="Ce dossier ne ressemble pas à un profil Claude (config.json et Local State manquants).",
        importar_sel="Importer la sélection", importados="{n} profil(s) importé(s).",
        dados_sec="Données", perigo="Zone de danger", apagar_tudo="Effacer toutes les données",
        apagar_tudo_sub="Efface tous les profils, connexions, sessions, photos ajoutées et réglages de Multi Claude. "
                        "Action irréversible.",
        apagar_confirma="Action irréversible. Pour confirmer, tapez {p} :", palavra="EFFACER",
        direitos="Logiciel libre sous licence GPL-3.0.", licenca="Licence", codigo="Code source",
        nao_oficial="Projet indépendant, sans lien avec Anthropic."),
    "de": dict(
        bv_titulo="Willkommen bei Multi Claude", continuar="Weiter",
        bv_sub="Wähle die Sprache der App. Du kannst sie später unter ⚙ Einstellungen ändern.",
        importar="Profile importieren", importar_sub="Auf diesem Computer gefundene Claude-Profile, die noch nicht in Multi Claude sind:",
        nenhum_encontrado="Keine neuen Profile gefunden. Wähle mit „Ordner suchen…“ eines manuell aus.",
        procurar_pasta="Ordner suchen…", procurar_db="perfis.db importieren…",
        pasta_invalida="Dieser Ordner ist kein Claude-Profil (config.json und Local State fehlen).",
        importar_sel="Auswahl importieren", importados="{n} Profil(e) importiert.",
        dados_sec="Daten", perigo="Gefahrenzone", apagar_tudo="Alle Daten löschen",
        apagar_tudo_sub="Löscht alle Profile, Anmeldungen, Sitzungen, hochgeladenen Bilder und Einstellungen von Multi Claude. "
                        "Kann nicht rückgängig gemacht werden.",
        apagar_confirma="Kann nicht rückgängig gemacht werden. Zum Bestätigen {p} eingeben:", palavra="LÖSCHEN",
        direitos="Freie Software unter der GPL-3.0-Lizenz.", licenca="Lizenz", codigo="Quellcode",
        nao_oficial="Unabhängiges Projekt, nicht mit Anthropic verbunden."),
    "it": dict(
        bv_titulo="Benvenuto in Multi Claude", continuar="Continua",
        bv_sub="Scegli la lingua dell'app. Puoi cambiarla in ⚙ Impostazioni.",
        importar="Importa profili", importar_sub="Profili di Claude trovati su questo computer non ancora in Multi Claude:",
        nenhum_encontrado="Nessun nuovo profilo trovato. Usa “Sfoglia cartella…” per sceglierne uno.",
        procurar_pasta="Sfoglia cartella…", procurar_db="Importa perfis.db…",
        pasta_invalida="Questa cartella non sembra un profilo di Claude (mancano config.json e Local State).",
        importar_sel="Importa selezionati", importados="{n} profilo/i importato/i.",
        dados_sec="Dati", perigo="Zona pericolosa", apagar_tudo="Cancella tutti i dati",
        apagar_tudo_sub="Cancella tutti i profili, accessi, sessioni, immagini caricate e impostazioni di Multi Claude. "
                        "Non si può annullare.",
        apagar_confirma="Non si può annullare. Per confermare, scrivi {p}:", palavra="ELIMINA",
        direitos="Software libero con licenza GPL-3.0.", licenca="Licenza", codigo="Codice sorgente",
        nao_oficial="Progetto indipendente, non affiliato ad Anthropic."),
    "ru": dict(
        bv_titulo="Добро пожаловать в Multi Claude", continuar="Продолжить",
        bv_sub="Выберите язык программы. Его можно изменить в ⚙ Настройках.",
        importar="Импорт профилей", importar_sub="Найденные на этом компьютере профили Claude, которых ещё нет в Multi Claude:",
        nenhum_encontrado="Новых профилей не найдено. Нажмите «Выбрать папку…», чтобы указать вручную.",
        procurar_pasta="Выбрать папку…", procurar_db="Импорт perfis.db…",
        pasta_invalida="Эта папка не похожа на профиль Claude (нет config.json и Local State).",
        importar_sel="Импортировать выбранные", importados="Импортировано профилей: {n}.",
        dados_sec="Данные", perigo="Опасная зона", apagar_tudo="Удалить все данные",
        apagar_tudo_sub="Удаляет все профили, входы, сессии, загруженные фото и настройки Multi Claude. "
                        "Отменить нельзя.",
        apagar_confirma="Отменить нельзя. Для подтверждения введите {p}:", palavra="УДАЛИТЬ",
        direitos="Свободное ПО под лицензией GPL-3.0.", licenca="Лицензия", codigo="Исходный код",
        nao_oficial="Независимый проект, не связан с Anthropic."),
    "ja": dict(
        bv_titulo="Multi Claude へようこそ", continuar="続行",
        bv_sub="アプリの言語を選んでください。あとで ⚙ 設定から変更できます。",
        importar="プロフィールを取り込む", importar_sub="このパソコンで見つかった、まだ Multi Claude にない Claude プロフィール：",
        nenhum_encontrado="新しいプロフィールは見つかりませんでした。「フォルダーを選択…」で手動で選べます。",
        procurar_pasta="フォルダーを選択…", procurar_db="perfis.db を取り込む…",
        pasta_invalida="このフォルダーは Claude のプロフィールではないようです（config.json と Local State がありません）。",
        importar_sel="選択したものを取り込む", importados="{n} 件のプロフィールを取り込みました。",
        dados_sec="データ", perigo="危険ゾーン", apagar_tudo="すべてのデータを消去",
        apagar_tudo_sub="Multi Claude のすべてのプロフィール、ログイン、セッション、追加した画像、設定を消去します。"
                        "元に戻せません。",
        apagar_confirma="元に戻せません。確認のため {p} と入力してください：", palavra="削除",
        direitos="GPL-3.0 ライセンスのフリーソフトウェアです。", licenca="ライセンス", codigo="ソースコード",
        nao_oficial="Anthropic とは無関係の独立したプロジェクトです。"),
    "zh": dict(
        bv_titulo="欢迎使用 Multi Claude", continuar="继续",
        bv_sub="请选择程序语言。之后可在 ⚙ 设置中更改。",
        importar="导入配置", importar_sub="在本机找到、尚未加入 Multi Claude 的 Claude 配置：",
        nenhum_encontrado="未找到新的配置。可点击“选择文件夹…”手动选择。",
        procurar_pasta="选择文件夹…", procurar_db="导入 perfis.db…",
        pasta_invalida="该文件夹不像 Claude 配置（缺少 config.json 和 Local State）。",
        importar_sel="导入所选", importados="已导入 {n} 个配置。",
        dados_sec="数据", perigo="危险区域", apagar_tudo="清除所有数据",
        apagar_tudo_sub="清除 Multi Claude 的所有配置、登录、会话、上传的图片和设置。此操作无法撤销。",
        apagar_confirma="此操作无法撤销。请输入 {p} 以确认：", palavra="删除",
        direitos="基于 GPL-3.0 许可证的自由软件。", licenca="许可证", codigo="源代码",
        nao_oficial="独立项目，与 Anthropic 无关。"),
    "ko": dict(
        bv_titulo="Multi Claude에 오신 것을 환영합니다", continuar="계속",
        bv_sub="프로그램 언어를 선택하세요. 나중에 ⚙ 설정에서 바꿀 수 있습니다.",
        importar="프로필 가져오기", importar_sub="이 컴퓨터에서 찾은, 아직 Multi Claude에 없는 Claude 프로필:",
        nenhum_encontrado="새 프로필을 찾지 못했습니다. “폴더 찾아보기…”로 직접 선택하세요.",
        procurar_pasta="폴더 찾아보기…", procurar_db="perfis.db 가져오기…",
        pasta_invalida="이 폴더는 Claude 프로필이 아닌 것 같습니다 (config.json과 Local State가 없음).",
        importar_sel="선택 항목 가져오기", importados="프로필 {n}개를 가져왔습니다.",
        dados_sec="데이터", perigo="위험 구역", apagar_tudo="모든 데이터 삭제",
        apagar_tudo_sub="Multi Claude의 모든 프로필, 로그인, 세션, 올린 사진과 설정을 지웁니다. 되돌릴 수 없습니다.",
        apagar_confirma="되돌릴 수 없습니다. 확인하려면 {p}을(를) 입력하세요:", palavra="삭제",
        direitos="GPL-3.0 라이선스의 자유 소프트웨어입니다.", licenca="라이선스", codigo="소스 코드",
        nao_oficial="Anthropic과 관련 없는 독립 프로젝트입니다."),
}
for _cod, _textos in _EXTRA.items():
    T[_cod].update(_textos)

for _cod, _aviso in {
    "pt": "As pastas vinculadas, compartilhadas ou não reconhecidas são preservadas.",
    "en": "Linked, shared and unrecognized folders are kept.",
    "es": "Se conservan las carpetas vinculadas, compartidas o no reconocidas.",
    "fr": "Les dossiers liés, partagés ou non reconnus sont conservés.",
    "de": "Verknüpfte, gemeinsam genutzte und unbekannte Ordner bleiben erhalten.",
    "it": "Le cartelle collegate, condivise o non riconosciute vengono conservate.",
    "ru": "Связанные, общие и нераспознанные папки сохраняются.",
    "ja": "リンク先、共有、未認識のフォルダーは保持されます。",
    "zh": "保留关联、共享和无法识别的文件夹。",
    "ko": "연결된 폴더, 공유 폴더 및 인식할 수 없는 폴더는 보존됩니다.",
}.items():
    T[_cod]["pastas_preservadas"] = _aviso


def log(msg):
    try:
        if os.path.exists(LOG) and os.path.getsize(LOG) > 500_000:
            os.replace(LOG, LOG + ".old")
        with open(LOG, "a", encoding="utf8") as f:
            f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} [{os.getpid()}] {msg}\n")
    except OSError:
        pass


# ---------- banco ----------

@contextlib.contextmanager
def db():
    """Conexão com perfis.db: confirma ao sair e sempre fecha (senão o Windows não deixa apagar o arquivo)."""
    os.makedirs(BASE, exist_ok=True)
    con = sqlite3.connect(DB, timeout=5)
    try:
        con.execute(
            "CREATE TABLE IF NOT EXISTS perfis ("
            "id INTEGER PRIMARY KEY, nome TEXT UNIQUE NOT NULL, pasta TEXT NOT NULL, "
            "criado TEXT DEFAULT CURRENT_TIMESTAMP)"
        )
        con.execute("CREATE TABLE IF NOT EXISTS config (chave TEXT PRIMARY KEY, valor TEXT)")
        try:
            con.execute("ALTER TABLE perfis ADD COLUMN foto TEXT")
        except sqlite3.OperationalError:
            pass  # coluna já existe
        yield con
        con.commit()
    except BaseException:
        con.rollback()
        raise
    finally:
        con.close()


def cfg_get(chave):
    with db() as con:
        r = con.execute("SELECT valor FROM config WHERE chave = ?", (chave,)).fetchone()
    return r[0] if r else None


def cfg_set(chave, valor):
    with db() as con:
        if valor is None:
            con.execute("DELETE FROM config WHERE chave = ?", (chave,))
        else:
            con.execute("INSERT OR REPLACE INTO config VALUES (?, ?)", (chave, valor))


_DISPOSITIVO = re.compile(r"(?:CON(?:IN\$|OUT\$)?|PRN|AUX|NUL|(?:COM|LPT)[0-9¹²³])(?:\.|$)", re.I)


def caminho_local(pasta, relativo=False):
    """Valida texto antes de consultar o Windows. O banco portátil também usa caminhos relativos."""
    if not isinstance(pasta, str) or not pasta:
        return False
    disco, resto = os.path.splitdrive(pasta)
    if disco:
        if not re.fullmatch("[A-Za-z]:", disco) or not resto.startswith(("\\", "/")):
            return False
    elif not relativo or resto.startswith(("\\", "/")):
        return False
    if re.search(r'[<>:"|?*\x00-\x1f]', resto):
        return False
    return all(not parte.endswith((" ", ".")) and not _DISPOSITIVO.match(parte)
               for parte in re.split(r"[\\/]", resto) if parte not in ("", ".", ".."))


def caminho_real(pasta, base=None):
    """Pastas dentro da pasta do programa ficam salvas como relativas (portátil)."""
    if not caminho_local(pasta, relativo=True):
        return ""
    return os.path.normpath(pasta if os.path.isabs(pasta) else os.path.join(base or BASE, pasta))


def caminho_salvo(pasta):
    pasta = os.path.abspath(pasta)
    try:
        rel = os.path.relpath(pasta, BASE)
        if not rel.startswith(".."):
            return rel
    except ValueError:  # outro disco
        pass
    return pasta


# ---------- perfis existentes na máquina ----------

def dentro_de(pasta, raiz):
    return bool(pasta and raiz) and (pasta == raiz or pasta.startswith(os.path.join(raiz, "")))


def inspecionar_caminho(pasta, resolver_links=False, limite=12):
    """Inspeciona os componentes a partir da raiz antes de acessar qualquer filho de uma junção."""
    if not caminho_local(pasta):
        return None
    partes = Path(os.path.normpath(pasta)).parts
    caminho = ""
    try:
        for indice, parte in enumerate(partes):
            caminho = os.path.join(caminho, parte)
            try:
                info = os.lstat(caminho)
            except FileNotFoundError:
                break
            reparse = getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
            if stat.S_ISLNK(info.st_mode) or reparse:
                if not resolver_links or not limite:
                    return None
                destino = os.readlink(caminho)
                if destino.startswith("\\\\?\\"):
                    destino = destino[4:]
                    if not caminho_local(destino):
                        return None
                if not caminho_local(destino, relativo=True):
                    return None
                alvo = os.path.join(os.path.dirname(caminho), destino, *partes[indice + 1:])
                return inspecionar_caminho(alvo, True, limite - 1)
        return normalizar(os.path.realpath(pasta)) if resolver_links else normalizar(pasta)
    except (OSError, ValueError):
        return None


def caminho_sem_desvios(pasta):
    return inspecionar_caminho(pasta) is not None


def identidade_pasta(pasta):
    return inspecionar_caminho(pasta, resolver_links=True)


def pasta_protegida(pasta):
    n = identidade_pasta(pasta)
    if not n:
        return True
    # Também protege pastas que contêm o programa e a pasta pessoal.
    if any(dentro_de(identidade_pasta(p), n) for p in (BASE, DADOS, FOTOS, os.path.expanduser("~"))):
        return True
    loc = os.environ.get("LOCALAPPDATA")
    roam = os.environ.get("APPDATA")
    raizes = [FOTOS]
    if roam:
        raizes.append(os.path.join(roam, "Claude"))
    if loc:
        raizes.extend(os.path.join(loc, nome) for nome in ("AnthropicClaude", "Claude", "Claude-Data"))
        pacotes = identidade_pasta(os.path.join(loc, "Packages"))
        if dentro_de(n, pacotes) and "claude" in os.path.relpath(n, pacotes).split(os.sep)[0].lower():
            return True
    return any(dentro_de(n, identidade_pasta(p)) for p in raizes)


def eh_perfil_claude(pasta):
    if not caminho_sem_desvios(pasta) or pasta_protegida(pasta):
        return False
    for nome in ("config.json", "Local State"):
        arquivo = os.path.join(pasta, nome)
        if not caminho_sem_desvios(arquivo) or not os.path.isfile(arquivo):
            return False
    return True


def validar_pasta_para_abrir(pasta):
    if caminho_sem_desvios(pasta) and not pasta_protegida(pasta):
        if os.path.dirname(normalizar(pasta)) == normalizar(DADOS) or eh_perfil_claude(pasta):
            return
    raise OSError(f"Invalid profile folder: {pasta}")


def detectar_perfis(registradas):
    """Pastas de perfil do Claude na máquina que ainda não estão cadastradas."""
    candidatas = []
    for raiz in (DADOS, os.path.join(os.path.expanduser("~"), "ClaudePerfis")):
        if not caminho_sem_desvios(raiz):
            continue
        try:
            candidatas += [os.path.join(raiz, n) for n in os.listdir(raiz)]
        except OSError:
            pass
    candidatas += list(claudes_abertos_caminhos())
    vistas, achadas = {identidade_pasta(p) for p in registradas}, []
    for c in candidatas:
        n = identidade_pasta(c)
        if not n or n in vistas or not eh_perfil_claude(c):
            continue
        vistas.add(n)
        achadas.append(c)
    return achadas


def identidade_para_apagar(perfil, perfis):
    """Só pastas diretas em dados, exclusivas deste cadastro, podem ser apagadas."""
    id_perfil, _, pasta, _ = perfil
    if os.path.dirname(normalizar(pasta)) != normalizar(DADOS) or not caminho_sem_desvios(pasta):
        return None
    alvo = identidade_pasta(pasta)
    if not alvo:
        return None
    for outro_id, _, outra, _ in perfis:
        if outro_id != id_perfil:
            outra = identidade_pasta(outra)
            if dentro_de(alvo, outra) or dentro_de(outra, alvo):
                return None
    try:
        info = os.lstat(pasta)
    except FileNotFoundError:
        return None
    if not stat.S_ISDIR(info.st_mode) or pasta_protegida(pasta):
        return None
    identidade = f"{info.st_dev}:{info.st_ino}"
    pendente = cfg_get(f"apagar_perfil_{id_perfil}")
    if pendente:
        return identidade if pendente == identidade else None
    return identidade if eh_perfil_claude(pasta) else None


def apagar_pasta_perfil(perfil, listar_perfis, identidade):
    pasta = perfil[2]
    fechar_claude(pasta)
    perfis = listar_perfis()
    if perfil not in perfis or identidade_para_apagar(perfil, perfis) != identidade:
        raise OSError(pasta)
    # Uma tentativa parcial pode ter removido os arquivos marcadores do Claude.
    cfg_set(f"apagar_perfil_{perfil[0]}", identidade)
    shutil.rmtree(pasta)


# ---------- fotos de perfil ----------

def preparar_fotos():
    """Cria profile_pictures e, no .exe, copia as fotos padrão embutidas que faltarem."""
    if not caminho_sem_desvios(FOTOS):
        return
    os.makedirs(FOTOS, exist_ok=True)
    embutidas = os.path.join(RECURSOS, "profile_pictures")
    if (os.path.normcase(embutidas) != os.path.normcase(FOTOS)
            and caminho_sem_desvios(embutidas) and os.path.isdir(embutidas)):
        for nome in os.listdir(embutidas):
            destino = os.path.join(FOTOS, nome)
            origem = os.path.join(embutidas, nome)
            if (nome.lower().endswith(EXT_IMAGEM) and not nome.startswith("usuario_")
                    and not os.path.lexists(destino) and caminho_sem_desvios(origem) and os.path.isfile(origem)):
                with open(origem, "rb") as fonte, open(destino, "xb") as copia:
                    shutil.copyfileobj(fonte, copia)


def listar_fotos():
    """Todas as imagens da pasta profile_pictures (as do app primeiro, as do usuário no fim)."""
    if not caminho_sem_desvios(FOTOS):
        return []
    try:
        nomes = [n for n in os.listdir(FOTOS) if foto_disponivel(n)]
    except OSError:
        return []
    return sorted(nomes, key=lambda n: (n.startswith("usuario_"), n.lower()))


def foto_disponivel(nome):
    if (not isinstance(nome, str) or "\x00" in nome or ":" in nome
            or nome != os.path.basename(nome) or not nome.lower().endswith(EXT_IMAGEM)):
        return False
    caminho = os.path.join(FOTOS, nome)
    return caminho_sem_desvios(caminho) and os.path.isfile(caminho)


def importar_banco(app, arquivo):
    if not caminho_sem_desvios(arquivo):
        raise OSError(f"Invalid database path: {arquivo}")
    with contextlib.closing(sqlite3.connect(Path(arquivo).absolute().as_uri() + "?mode=ro", uri=True)) as origem:
        colunas = [c[1] for c in origem.execute("PRAGMA table_info(perfis)")]
        fotos = "foto" if "foto" in colunas else "NULL"
        linhas = origem.execute(f"SELECT nome, pasta, {fotos} FROM perfis").fetchall()
    registradas = {identidade_pasta(p[2]) for p in app.perfis()}
    n = 0
    with db() as con:
        for nome, pasta, foto in linhas:
            if not isinstance(nome, str) or not nome.strip() or not isinstance(pasta, str) or not pasta.strip():
                continue
            if "\x00" in nome or "\x00" in pasta:
                continue
            pasta = caminho_real(pasta, os.path.dirname(arquivo))
            if not eh_perfil_claude(pasta):
                continue
            chave = identidade_pasta(pasta)
            if chave in registradas:
                continue
            app._inserir(con, app.nome_livre(con, nome), pasta, foto if foto_disponivel(foto) else None)
            registradas.add(chave)
            n += 1
    return n


def importar_foto(caminho):
    """Copia uma imagem escolhida pelo usuário para profile_pictures (recortada em quadrado)."""
    if not caminho_sem_desvios(FOTOS):
        raise OSError(f"Invalid gallery folder: {FOTOS}")
    with Image.open(caminho) as original:
        img = original.convert("RGBA" if original.mode in ("RGBA", "LA", "P") else "RGB")
    lado = min(img.size)
    x, y = (img.width - lado) // 2, (img.height - lado) // 2
    img = img.crop((x, y, x + lado, y + lado)).resize((512, 512), Image.LANCZOS)
    base = re.sub(r"[^\w-]+", "_", os.path.splitext(os.path.basename(caminho))[0]).strip("_")[:40] or "foto"
    fd, destino = tempfile.mkstemp(dir=FOTOS, prefix=f"usuario_{base}_", suffix=".png")
    try:
        with os.fdopen(fd, "wb") as arquivo:
            img.save(arquivo, format="PNG")
    except BaseException:
        os.remove(destino)
        raise
    return os.path.basename(destino)


_cache_img = {}


def avatar(foto, tam, fundo):
    """PhotoImage redondo da foto (ou None se não der). Fica em cache."""
    if not Image or not foto_disponivel(foto):
        return None
    caminho = os.path.join(FOTOS, foto)
    try:
        chave = (caminho, os.path.getmtime(caminho), tam, fundo)
    except OSError:
        return None
    if chave in _cache_img:
        return _cache_img[chave]
    try:
        img = Image.open(caminho)
        img.seek(0)
        img = img.convert("RGBA")
        lado = min(img.size)
        x, y = (img.width - lado) // 2, (img.height - lado) // 2
        f = 4  # supersampling para borda suave
        img = img.crop((x, y, x + lado, y + lado)).resize((tam * f, tam * f), Image.LANCZOS)
        mask = Image.new("L", (tam * f, tam * f), 0)
        ImageDraw.Draw(mask).ellipse((0, 0, tam * f - 1, tam * f - 1), fill=255)
        out = Image.new("RGBA", (tam * f, tam * f), fundo)
        out.paste(img, (0, 0), Image.composite(img.split()[3], Image.new("L", mask.size, 0), mask))
        out = out.resize((tam, tam), Image.LANCZOS)
        foto_tk = ImageTk.PhotoImage(out)
    except Exception as e:
        log(f"foto inválida {foto}: {e}")
        return None
    _cache_img[chave] = foto_tk
    return foto_tk


def desenhar_avatar(canvas, nome, foto, tam, fundo, indice):
    canvas.delete("all")
    img = avatar(foto, tam, fundo)
    if img:
        canvas.create_image(tam // 2, tam // 2, image=img)
        canvas.imagem = img  # mantém referência
    else:
        canvas.create_oval(1, 1, tam - 1, tam - 1, fill=AVATARES[indice % len(AVATARES)], width=0)
        canvas.create_text(tam // 2, tam // 2, text=(nome.strip()[:1] or "?").upper(), fill="#ffffff",
                           font=(FONTE, int(tam * 0.36), "bold"))


# ---------- Claude ----------

def achar_claude():
    alias = os.path.join(os.environ["LOCALAPPDATA"], "Microsoft", "WindowsApps", "claude-desktop.exe")
    if os.path.exists(alias):
        return alias
    p = os.path.join(os.environ["LOCALAPPDATA"], "AnthropicClaude", "claude.exe")
    if os.path.exists(p):
        return p
    try:
        loc = subprocess.check_output(
            [os.path.join(os.environ["SystemRoot"], "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
             "-NoProfile", "-NonInteractive", "-Command", "(Get-AppxPackage Claude).InstallLocation"],
            text=True, creationflags=subprocess.CREATE_NO_WINDOW, timeout=8,
        ).strip()
        if os.path.isabs(loc):
            exe = os.path.join(loc, "app", "claude.exe")
            if os.path.isfile(exe):
                return exe
    except Exception:
        pass
    return None


def abrir_claude(claude, pasta, url=None):
    validar_pasta_para_abrir(pasta)
    os.makedirs(pasta, exist_ok=True)
    args = [claude, f"--user-data-dir={pasta}"]
    if url:
        args.append(url)
    subprocess.Popen(args)


def esta_logado(pasta):
    """Lê a última transição de login no log do Claude do perfil."""
    if not pasta or not caminho_sem_desvios(pasta):
        return False
    try:
        diario = os.path.join(pasta, "logs", "main.log")
        if not caminho_sem_desvios(diario):
            return False
        with open(diario, "rb") as f:
            f.seek(0, 2)
            f.seek(max(0, f.tell() - 2_000_000))
            texto = f.read().decode("utf8", "replace")
    except OSError:
        texto = ""
    m = re.findall(r"Login-state transition \(loggedOut: \w+ → (\w+)", texto)
    if m:
        return m[-1] == "false"
    try:
        cfg = os.path.join(pasta, "config.json")
        if not caminho_sem_desvios(cfg):
            return False
        with open(cfg, encoding="utf8") as f:
            config = json.load(f)
            return isinstance(config, dict) and bool(config.get("lastKnownAccountUuid"))
    except (OSError, ValueError):
        return False


def deslogar(pasta):
    """Apaga cookies e tokens do perfil (o Claude dele precisa estar fechado)."""
    cfg = os.path.join(pasta, "config.json")
    diario = os.path.join(pasta, "logs", "main.log")
    cookies = [os.path.join(pasta, "Network", nome)
               for nome in ("Cookies", "Cookies-journal", "Cookies-wal", "Cookies-shm")]
    for arquivo in [cfg, diario, *cookies]:
        if not caminho_sem_desvios(arquivo):
            raise OSError(f"Profile path is redirected: {arquivo}")
    if os.path.isfile(diario) and os.stat(diario).st_nlink > 1:
        raise OSError(f"Shared profile log: {diario}")
    try:
        with open(cfg, encoding="utf8") as f:
            d = json.load(f)
    except FileNotFoundError:
        d = {}
    if not isinstance(d, dict):
        raise ValueError(f"Invalid profile configuration: {cfg}")
    for arquivo in cookies:
        with contextlib.suppress(FileNotFoundError):
            os.remove(arquivo)
    limpo = {k: v for k, v in d.items() if not k.startswith("oauth:") and k != "lastKnownAccountUuid"}
    if limpo != d:
        with tempfile.NamedTemporaryFile(dir=pasta, delete=False) as tmp:
            temporario = tmp.name
        try:
            with open(temporario, "w", encoding="utf8") as f:
                json.dump(limpo, f, indent=2)
            os.replace(temporario, cfg)
        finally:
            with contextlib.suppress(FileNotFoundError):
                os.remove(temporario)
    if os.path.isfile(diario):
        with open(diario, "a", encoding="utf8") as f:
            f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} [info] [multi-claude] "
                    "Login-state transition (loggedOut: false → true)\n")


# ----- processos -----

class _PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_void_p), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_wchar * 260)]


class _UStr(ctypes.Structure):
    _fields_ = [("Length", ctypes.c_ushort), ("MaximumLength", ctypes.c_ushort), ("Buffer", ctypes.c_void_p)]


_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
_k32.OpenProcess.restype = wt.HANDLE
_ntdll = ctypes.WinDLL("ntdll")


def pids_por_nome(nome):
    snap = _k32.CreateToolhelp32Snapshot(2, 0)  # TH32CS_SNAPPROCESS
    pids = set()
    if not snap or snap == wt.HANDLE(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        e = _PE32()
        e.dwSize = ctypes.sizeof(e)
        ok = _k32.Process32FirstW(wt.HANDLE(snap), ctypes.byref(e))
        while ok:
            if e.szExeFile.lower() == nome:
                pids.add(e.th32ProcessID)
            ok = _k32.Process32NextW(wt.HANDLE(snap), ctypes.byref(e))
        if ctypes.get_last_error() != 18:  # ERROR_NO_MORE_FILES
            raise ctypes.WinError(ctypes.get_last_error())
    finally:
        _k32.CloseHandle(wt.HANDLE(snap))
    return pids


def linha_de_comando(pid):
    h = _k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return ""
    try:
        buf = ctypes.create_string_buffer(65536)
        ret = wt.ULONG()
        # 60 = ProcessCommandLineInformation (Windows 8.1+)
        if _ntdll.NtQueryInformationProcess(wt.HANDLE(h), 60, buf, len(buf), ctypes.byref(ret)) != 0:
            return ""
        us = _UStr.from_buffer(buf)
        return ctypes.wstring_at(us.Buffer, us.Length // 2) if us.Buffer else ""
    finally:
        _k32.CloseHandle(wt.HANDLE(h))


def normalizar(pasta):
    return os.path.normcase(os.path.normpath(pasta))


def _claudes_com_pasta(estrito=False):
    separar = ctypes.windll.shell32.CommandLineToArgvW
    separar.argtypes = (wt.LPCWSTR, ctypes.POINTER(ctypes.c_int))
    separar.restype = ctypes.POINTER(wt.LPWSTR)
    indeterminados = set()
    for pid in pids_por_nome("claude.exe"):
        cmd = linha_de_comando(pid)
        if not cmd:
            indeterminados.add(pid)
            continue
        if "--user-data-dir=" not in cmd:
            continue
        n = ctypes.c_int()
        argv = separar(cmd, ctypes.byref(n))
        if not argv:
            indeterminados.add(pid)
            continue
        try:
            args = argv[1:n.value]
        finally:
            _k32.LocalFree(ctypes.cast(argv, wt.HLOCAL))
        if any(a.startswith("--type=") for a in args):
            continue
        pastas = [a.split("=", 1)[1] for a in args if a.startswith("--user-data-dir=")]
        if len(pastas) == 1 and os.path.isabs(pastas[0]):
            yield pid, pastas[0]
        elif pastas:
            indeterminados.add(pid)
    if estrito and indeterminados and indeterminados.intersection(pids_por_nome("claude.exe")):
        raise OSError("Close Claude Desktop before changing profile data. Windows could not verify all running instances.")


def claudes_abertos(estrito=False):
    """{pasta do perfil normalizada: pid do processo principal} dos Claude abertos com --user-data-dir."""
    return {normalizar(pasta): pid for pid, pasta in _claudes_com_pasta(estrito)}


def claudes_abertos_caminhos():
    return {pasta for _, pasta in _claudes_com_pasta()}


def fechar_claude(pasta):
    pid = claudes_abertos(estrito=True).get(normalizar(pasta))
    if pid:
        # Solicita fechamento normal para o Electron terminar as gravações pendentes.
        taskkill = os.path.join(os.environ["SystemRoot"], "System32", "taskkill.exe")
        resultado = subprocess.run([taskkill, "/PID", str(pid)], capture_output=True,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        for _ in range(30):
            if normalizar(pasta) not in claudes_abertos(estrito=True):
                return
            if resultado.returncode:
                break
            time.sleep(0.1)
        raise OSError(f"Close Claude Desktop for this profile and try again.\n{pasta}")


# ---------- bandeiras ----------

def desenhar_bandeira(c, cod, w=24, h=16):
    def faixas_h(*cores):
        for i, cor in enumerate(cores):
            c.create_rectangle(0, i * h / len(cores), w, (i + 1) * h / len(cores), fill=cor, width=0)

    def faixas_v(*cores):
        for i, cor in enumerate(cores):
            c.create_rectangle(i * w / len(cores), 0, (i + 1) * w / len(cores), h, fill=cor, width=0)

    if cod == "pt":  # Brasil
        c.create_rectangle(0, 0, w, h, fill="#009c3b", width=0)
        c.create_polygon(w / 2, 2, w - 2, h / 2, w / 2, h - 2, 2, h / 2, fill="#ffdf00", width=0)
        c.create_oval(w / 2 - 4, h / 2 - 4, w / 2 + 4, h / 2 + 4, fill="#002776", width=0)
    elif cod == "en":  # EUA
        for i in range(13):
            c.create_rectangle(0, i * h / 13, w, (i + 1) * h / 13,
                               fill="#b22234" if i % 2 == 0 else "#ffffff", width=0)
        c.create_rectangle(0, 0, w * 0.45, h * 7 / 13, fill="#3c3b6e", width=0)
    elif cod == "es":
        c.create_rectangle(0, 0, w, h, fill="#aa151b", width=0)
        c.create_rectangle(0, h / 4, w, h * 3 / 4, fill="#f1bf00", width=0)
    elif cod == "fr":
        faixas_v("#0055a4", "#ffffff", "#ef4135")
    elif cod == "de":
        faixas_h("#000000", "#dd0000", "#ffce00")
    elif cod == "it":
        faixas_v("#009246", "#ffffff", "#ce2b37")
    elif cod == "ru":
        faixas_h("#ffffff", "#0039a6", "#d52b1e")
    elif cod == "ja":
        c.create_rectangle(0, 0, w, h, fill="#ffffff", width=0)
        c.create_oval(w / 2 - 5, h / 2 - 5, w / 2 + 5, h / 2 + 5, fill="#bc002d", width=0)
    elif cod == "zh":
        c.create_rectangle(0, 0, w, h, fill="#de2910", width=0)
        c.create_text(6, 6, text="★", fill="#ffde00", font=(FONTE, 7))
    elif cod == "ko":
        c.create_rectangle(0, 0, w, h, fill="#ffffff", width=0)
        x, y, r = w / 2, h / 2, 4.5
        c.create_arc(x - r, y - r, x + r, y + r, start=0, extent=180, fill="#cd2e3a", width=0)
        c.create_arc(x - r, y - r, x + r, y + r, start=180, extent=180, fill="#0047a0", width=0)
        for x0 in (2, w - 5):
            for y0 in (2, h - 5):
                c.create_rectangle(x0, y0, x0 + 3, y0 + 3, fill="#000000", width=0)
    c.create_rectangle(0, 0, w - 1, h - 1, outline=C.get("BORDA", "#888888"))


# ---------- componentes ----------

class Botao(tk.Label):
    """Botão plano com hover. tipo: 'primario', 'normal' ou 'fantasma'."""

    def __init__(self, master, texto, cmd, tipo="normal", tamanho=10, **kw):
        cores = {"primario": (C["ACCENT"], C["ACCENT_HOVER"], "#ffffff"),
                 "normal": (C["BTN"], C["BTN_HOVER"], C["TEXTO"]),
                 "fantasma": (master["bg"], C["BTN"], C["TEXTO"])}[tipo]
        self.cor, self.hover = cores[0], cores[1]
        kw.setdefault("padx", 14)
        kw.setdefault("pady", 7)
        super().__init__(master, text=texto, bg=self.cor, fg=cores[2], font=(FONTE, tamanho, "bold"),
                         cursor="hand2", **kw)
        self.bind("<Button-1>", lambda e: cmd())
        self.bind("<Enter>", lambda e: self.config(bg=self.hover))
        self.bind("<Leave>", lambda e: self.config(bg=self.cor))


class Rolavel(tk.Frame):
    """Frame com rolagem vertical (roda do mouse) que ajusta a largura do conteúdo."""

    def __init__(self, master, bg):
        super().__init__(master, bg=bg)
        self.tela = tk.Canvas(self, bg=bg, highlightthickness=0, bd=0)
        self.tela.pack(fill="both", expand=True)
        self.dentro = tk.Frame(self.tela, bg=bg)
        self._janela = self.tela.create_window(0, 0, window=self.dentro, anchor="nw")
        self.dentro.bind("<Configure>", lambda e: self.tela.configure(scrollregion=self.tela.bbox("all")))
        self.tela.bind("<Configure>", lambda e: self.tela.itemconfigure(self._janela, width=e.width))
        self.bind_all("<MouseWheel>", self._roda, add="+")

    def _roda(self, e):
        try:
            if str(e.widget).startswith(str(self)) and self.dentro.winfo_height() > self.tela.winfo_height():
                self.tela.yview_scroll(int(-e.delta / 120), "units")
        except (tk.TclError, AttributeError):
            pass


class Janela(tk.Toplevel):
    """Diálogo modal no tema do app."""

    def __init__(self, app, titulo, largura=420, altura=None):
        super().__init__(app)
        self.app = app
        self.withdraw()
        self.title(titulo)
        self.configure(bg=C["PAINEL"])
        self.transient(app)
        self.resizable(True, True)
        por_icone(self)
        self.bind("<Escape>", lambda e: self.destroy())
        self._tam = (largura, altura)

    def mostrar(self):
        self.update_idletasks()
        w, h = self._tam
        w = max(w, self.winfo_reqwidth())
        h = h or self.winfo_reqheight()
        x = self.app.winfo_rootx() + (self.app.winfo_width() - w) // 2
        y = self.app.winfo_rooty() + (self.app.winfo_height() - h) // 3
        self.geometry(f"{w}x{h}+{max(0, x)}+{max(0, y)}")
        self.minsize(min(w, 340), min(h, 200))
        barra_titulo(self)
        self.deiconify()
        self.grab_set()
        self.focus_force()


def dialogo(app, titulo, msg, entrada=None, botoes=None, ocultar=False):
    """Mostra uma mensagem; com `entrada` (texto inicial) pede um texto. Devolve o valor do botão/texto."""
    j = Janela(app, titulo)
    res = {"v": None}
    corpo = tk.Frame(j, bg=C["PAINEL"])
    corpo.pack(fill="both", expand=True, padx=22, pady=(20, 8))
    tk.Label(corpo, text=msg, bg=C["PAINEL"], fg=C["TEXTO"], font=(FONTE, 10), justify="left",
             wraplength=370, anchor="w").pack(fill="x")
    campo = None
    if entrada is not None:
        borda = tk.Frame(corpo, bg=C["BORDA"])
        borda.pack(fill="x", pady=(12, 0))
        campo = tk.Entry(borda, font=(FONTE, 11), bg=C["CARD"], fg=C["TEXTO"], insertbackground=C["TEXTO"],
                         relief="flat", highlightthickness=0, show="•" if ocultar else "", exportselection=False)
        campo.pack(fill="x", padx=1, pady=1, ipady=6, ipadx=6)
        campo.insert(0, entrada)
        campo.select_range(0, "end")
        campo.focus_set()
    rodape = tk.Frame(j, bg=C["PAINEL"])
    rodape.pack(fill="x", padx=22, pady=(8, 18))
    botoes = botoes or [(app.t("ok"), True, "primario")]

    def escolher(v):
        res["v"] = (campo.get().strip() if v is True and campo is not None else v)
        j.destroy()

    for texto, valor, tipo in reversed(botoes):
        Botao(rodape, texto, lambda v=valor: escolher(v), tipo=tipo).pack(side="right", padx=(8, 0))
    j.bind("<Return>", lambda e: escolher(botoes[0][1]))
    j.mostrar()
    if campo is not None:
        campo.focus_set()
    j.wait_window()
    return res["v"]


# ---------- app ----------

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(APP)
        por_icone(self)
        self.claude = achar_claude()
        self.login_perfil = None
        self.login_timer = None
        self.estado = {}  # pasta -> (logado, aberto)
        self.layout = None  # (colunas, compacto) do último desenho
        self.primeira = cfg_get("idioma") is None
        self.lang = cfg_get("idioma") or idioma_do_windows()
        if self.lang not in T:
            self.lang = "en"
        self.tema = cfg_get("tema")
        if self.tema not in (*TEMAS, "sistema"):
            self.tema = "sistema"
        self.tema_efetivo = aplicar_tema(self.tema)

        geo = cfg_get("geometria")
        try:
            self.geometry(geo or "640x680")
        except tk.TclError:
            self.geometry("640x680")
        self.minsize(440, 480)

        # Sobrou modo login de uma execução anterior? Limpa.
        for c in ("login_pasta", "login_ok", "login_url"):
            cfg_set(c, None)
        preparar_fotos()

        self.montar()
        self.protocol("WM_DELETE_WINDOW", self.fechar)
        self.after(200, self.atualizar_estado)
        if self.primeira:
            self.after(300, self.boas_vindas)

    def t(self, chave, **kw):
        s = T[self.lang].get(chave, T["en"][chave])
        return s.format(**kw) if kw else s

    # ----- montagem -----
    def montar(self):
        for w in self.winfo_children():
            w.destroy()
        self.configure(bg=C["BG"])
        barra_titulo(self)
        self.layout = None

        topo = tk.Frame(self, bg=C["BG"])
        topo.pack(fill="x", padx=22, pady=(18, 0))
        logo = tk.Canvas(topo, width=40, height=40, bg=C["BG"], highlightthickness=0)
        logo.pack(side="left", padx=(0, 12))
        img = None
        for arq in (ICONE_PNG, ICONE):
            if Image and os.path.exists(arq):
                try:
                    img = ImageTk.PhotoImage(Image.open(arq).convert("RGBA").resize((40, 40), Image.LANCZOS))
                    break
                except Exception:
                    img = None
        if img:
            logo.create_image(20, 20, image=img)
            logo.imagem = img
        else:
            logo.create_text(20, 20, text="✳", fill=C["ACCENT"], font=(FONTE, 22, "bold"))
        # Os botões entram primeiro no pack para nunca serem cortados quando a janela estreita.
        acoes = tk.Frame(topo, bg=C["BG"])
        acoes.pack(side="right")
        Botao(acoes, "⚙", self.configuracoes, tipo="fantasma", tamanho=13, padx=9, pady=2).pack(side="left", padx=(0, 6))
        self.btn_novo = Botao(acoes, "+  " + self.t("novo"), self.novo, tipo="primario")
        self.btn_novo.pack(side="left")
        textos = tk.Frame(topo, bg=C["BG"])
        textos.pack(side="left", fill="x", expand=True)
        tk.Label(textos, text=APP, bg=C["BG"], fg=C["TEXTO"], font=(FONTE, 18, "bold"), anchor="w").pack(fill="x")
        self.lbl_sub = tk.Label(textos, text=self.t("sub"), bg=C["BG"], fg=C["SUB"], font=(FONTE, 9),
                                anchor="w", justify="left")
        self.lbl_sub.pack(fill="x")
        textos.bind("<Configure>", lambda e: self.lbl_sub.config(wraplength=max(120, e.width)))
        topo.bind("<Configure>", lambda e: self.btn_novo.config(
            text="+" if e.width < 520 else "+  " + self.t("novo")))

        if not self.claude:
            tk.Label(self, text="⚠  " + self.t("ausente"), bg=C["BG"], fg=C["VERMELHO"],
                     font=(FONTE, 9, "bold"), anchor="w").pack(fill="x", padx=24, pady=(10, 0))

        # Barra de status do login
        self.status = tk.Frame(self, bg=C["PAINEL"], highlightthickness=1, highlightbackground=C["ACCENT"])
        self.status_txt = tk.Label(self.status, bg=C["PAINEL"], fg=C["TEXTO"], font=(FONTE, 10),
                                   justify="left", anchor="w")
        self.status_txt.pack(side="left", fill="x", expand=True, padx=14, pady=10)
        self.status_btns = tk.Frame(self.status, bg=C["PAINEL"])
        Botao(self.status_btns, self.t("colar"), self.colar_link, tipo="primario", tamanho=9).pack(fill="x", pady=2)
        Botao(self.status_btns, self.t("cancelar"), self.cancelar_login, tamanho=9).pack(fill="x", pady=2)
        self.status.bind("<Configure>", lambda e: self.status_txt.config(wraplength=max(200, e.width - 170)))

        # Rodapé com copyright
        rodape = tk.Frame(self, bg=C["BG"])
        rodape.pack(side="bottom", fill="x", padx=22, pady=(0, 10))
        tk.Label(rodape, text=f"{APP} v{VERSAO} · Modified edition", bg=C["BG"], fg=C["SUB"],
                 font=(FONTE, 8)).pack(side="left")
        link = tk.Label(rodape, text=ATRIBUICAO, bg=C["BG"], fg=C["ACCENT"], font=(FONTE, 8, "underline"),
                        cursor="hand2")
        link.pack(side="right")
        link.bind("<Button-1>", lambda e: webbrowser.open(GITHUB))

        self.corpo = Rolavel(self, C["BG"])
        self.corpo.pack(fill="both", expand=True, padx=(22, 16), pady=14)
        self.area = self.corpo.dentro
        self.corpo.tela.bind("<Configure>", self._ao_redimensionar, add="+")

        self.recarregar()
        if self.login_perfil:
            self.mostrar_status(self.t("aguardando", n=self.login_perfil[1]), True)

    def _layout_para(self, largura):
        colunas = 2 if largura >= 860 else 1
        compacto = largura / colunas < 470
        return colunas, compacto

    def _ao_redimensionar(self, e):
        if self._layout_para(e.width) != self.layout:
            self.after_idle(self.recarregar)

    # ----- configurações -----
    def configuracoes(self):
        j = Janela(self, self.t("config"), largura=460, altura=560)
        rol = Rolavel(j, C["PAINEL"])
        rol.pack(fill="both", expand=True)
        corpo = tk.Frame(rol.dentro, bg=C["PAINEL"])
        corpo.pack(fill="both", expand=True, padx=22, pady=18)

        def secao(txt):
            tk.Label(corpo, text=txt.upper(), bg=C["PAINEL"], fg=C["SUB"], font=(FONTE, 8, "bold"),
                     anchor="w").pack(fill="x", pady=(14, 6))

        secao(self.t("aparencia"))
        seg = tk.Frame(corpo, bg=C["BTN"])
        seg.pack(fill="x")
        for cod, icone in (("claro", "☀"), ("escuro", "☾"), ("sistema", "◐")):
            ativo = cod == self.tema
            b = tk.Label(seg, text=f"{icone}  {self.t(cod)}", bg=C["ACCENT"] if ativo else C["BTN"],
                         fg="#ffffff" if ativo else C["TEXTO"], font=(FONTE, 10, "bold"), pady=8, cursor="hand2")
            b.pack(side="left", fill="x", expand=True, padx=2, pady=2)
            b.bind("<Button-1>", lambda e, cod=cod: (j.destroy(), self.mudar_tema(cod)))

        secao(self.t("idioma"))
        grade = tk.Frame(corpo, bg=C["PAINEL"])
        grade.pack(fill="x")
        for col in (0, 1):
            grade.columnconfigure(col, weight=1, uniform="i")
        for i, (cod, nome) in enumerate(IDIOMAS):
            ativo = cod == self.lang
            cor = C["ACCENT"] if ativo else C["CARD"]
            linha = tk.Frame(grade, bg=cor, cursor="hand2", highlightthickness=1,
                             highlightbackground=C["ACCENT"] if ativo else C["BORDA"])
            linha.grid(row=i // 2, column=i % 2, sticky="ew", padx=3, pady=3)
            cv = tk.Canvas(linha, width=24, height=16, bg=cor, highlightthickness=0, cursor="hand2")
            desenhar_bandeira(cv, cod)
            cv.pack(side="left", padx=(10, 8), pady=8)
            lb = tk.Label(linha, text=nome, bg=cor, fg="#ffffff" if ativo else C["TEXTO"], font=(FONTE, 10),
                          anchor="w", cursor="hand2")
            lb.pack(side="left", fill="x", expand=True)
            for w in (linha, cv, lb):
                w.bind("<Button-1>", lambda e, cod=cod: (j.destroy(), self.mudar_idioma(cod)))

        secao(self.t("sobre"))
        sobre = tk.Frame(corpo, bg=C["CARD"], highlightthickness=1, highlightbackground=C["BORDA"])
        sobre.pack(fill="x")
        tk.Label(sobre, text=f"{APP} · Modified edition", bg=C["CARD"], fg=C["TEXTO"], font=(FONTE, 12, "bold"),
                 anchor="w").pack(fill="x", padx=14, pady=(12, 0))
        tk.Label(sobre, text=f"{self.t('versao')} {VERSAO}", bg=C["CARD"], fg=C["SUB"], font=(FONTE, 9),
                 anchor="w").pack(fill="x", padx=14)
        tk.Label(sobre, text=f"{ATRIBUICAO}\n{COPYRIGHT}. {self.t('direitos')}", justify="left",
                 bg=C["CARD"], fg=C["SUB"], font=(FONTE, 9),
                 anchor="w").pack(fill="x", padx=14, pady=(6, 0))
        tk.Label(sobre, text=self.t("nao_oficial"), bg=C["CARD"], fg=C["SUB"], font=(FONTE, 9),
                 anchor="w").pack(fill="x", padx=14)
        links = tk.Frame(sobre, bg=C["CARD"])
        links.pack(fill="x", padx=14, pady=(6, 12))
        for texto, url in ((self.t("codigo"), REPO), (self.t("licenca") + " GPL-3.0", REPO + "/blob/main/LICENSE"),
                           ("github.com/RichardKPL", GITHUB)):
            lk = tk.Label(links, text=texto, bg=C["CARD"], fg=C["ACCENT"], font=(FONTE, 9, "underline"),
                          cursor="hand2")
            lk.pack(side="left", padx=(0, 14))
            lk.bind("<Button-1>", lambda e, url=url: webbrowser.open(url))

        secao(self.t("dados_sec"))
        Botao(corpo, "⇩  " + self.t("importar"), lambda: (j.destroy(), self.importar())).pack(anchor="w")

        secao(self.t("perigo"))
        perigo = tk.Frame(corpo, bg=C["CARD"], highlightthickness=1, highlightbackground=C["VERMELHO"])
        perigo.pack(fill="x", pady=(0, 6))
        tk.Label(perigo, text=self.t("apagar_tudo"), bg=C["CARD"], fg=C["VERMELHO"], font=(FONTE, 11, "bold"),
                 anchor="w").pack(fill="x", padx=14, pady=(12, 0))
        tk.Label(perigo, text=self.t("apagar_tudo_sub"), bg=C["CARD"], fg=C["SUB"], font=(FONTE, 9),
                 anchor="w", justify="left", wraplength=360).pack(fill="x", padx=14, pady=(2, 8))
        b = Botao(perigo, self.t("apagar_tudo"), lambda: (j.destroy(), self.apagar_tudo()))
        b.cor = b.hover = C["VERMELHO"]
        b.config(bg=C["VERMELHO"], fg="#ffffff")
        b.pack(anchor="w", padx=14, pady=(0, 14))
        j.mostrar()

    # ----- primeira abertura -----
    def boas_vindas(self):
        j = Janela(self, APP, largura=500, altura=520)
        j.protocol("WM_DELETE_WINDOW", lambda: concluir())
        corpo = tk.Frame(j, bg=C["PAINEL"])
        corpo.pack(fill="both", expand=True, padx=26, pady=22)
        logo = tk.Canvas(corpo, width=64, height=64, bg=C["PAINEL"], highlightthickness=0)
        logo.pack()
        try:
            img = ImageTk.PhotoImage(Image.open(ICONE_PNG).convert("RGBA").resize((64, 64), Image.LANCZOS))
            logo.create_image(32, 32, image=img)
            logo.imagem = img
        except Exception:
            logo.create_text(32, 32, text="✳", fill=C["ACCENT"], font=(FONTE, 30, "bold"))
        titulo = tk.Label(corpo, bg=C["PAINEL"], fg=C["TEXTO"], font=(FONTE, 16, "bold"))
        titulo.pack(pady=(10, 2))
        sub = tk.Label(corpo, bg=C["PAINEL"], fg=C["SUB"], font=(FONTE, 10), wraplength=430, justify="center")
        sub.pack(pady=(0, 14))
        grade = tk.Frame(corpo, bg=C["PAINEL"])
        grade.pack(fill="x")
        for col in (0, 1):
            grade.columnconfigure(col, weight=1, uniform="i")
        botao = Botao(corpo, "", lambda: concluir(), tipo="primario")
        botao.pack(fill="x", pady=(18, 0))

        def desenhar():
            titulo.config(text=self.t("bv_titulo"))
            sub.config(text=self.t("bv_sub"))
            botao.config(text=self.t("continuar") + "  →")
            for w in grade.winfo_children():
                w.destroy()
            for i, (cod, nome) in enumerate(IDIOMAS):
                ativo = cod == self.lang
                cor = C["ACCENT"] if ativo else C["CARD"]
                linha = tk.Frame(grade, bg=cor, cursor="hand2", highlightthickness=1,
                                 highlightbackground=C["ACCENT"] if ativo else C["BORDA"])
                linha.grid(row=i // 2, column=i % 2, sticky="ew", padx=3, pady=3)
                cv = tk.Canvas(linha, width=24, height=16, bg=cor, highlightthickness=0, cursor="hand2")
                desenhar_bandeira(cv, cod)
                cv.pack(side="left", padx=(10, 8), pady=8)
                lb = tk.Label(linha, text=nome, bg=cor, fg="#ffffff" if ativo else C["TEXTO"],
                              font=(FONTE, 10), anchor="w", cursor="hand2")
                lb.pack(side="left", fill="x", expand=True)
                for w in (linha, cv, lb):
                    w.bind("<Button-1>", lambda e, cod=cod: escolher(cod))

        def escolher(cod):
            self.lang = cod
            desenhar()

        def concluir():
            cfg_set("idioma", self.lang)
            self.primeira = False
            j.destroy()
            self.montar()
            # Já existem perfis do Claude nesta máquina? Oferece importar.
            if detectar_perfis({normalizar(p[2]) for p in self.perfis()}):
                self.importar()

        desenhar()
        j.unbind("<Escape>")
        j.mostrar()

    # ----- importar perfis -----
    def importar(self):
        j = Janela(self, self.t("importar"), largura=560, altura=500)
        tk.Label(j, text=self.t("importar_sub"), bg=C["PAINEL"], fg=C["TEXTO"], font=(FONTE, 10),
                 anchor="w", justify="left", wraplength=500).pack(fill="x", padx=20, pady=(18, 8))
        rodape = tk.Frame(j, bg=C["PAINEL"])
        rodape.pack(side="bottom", fill="x", padx=20, pady=14)
        rol = Rolavel(j, C["PAINEL"])
        rol.pack(fill="both", expand=True, padx=20)
        marcados = {}

        def listar():
            for w in rol.dentro.winfo_children():
                w.destroy()
            registradas = {identidade_pasta(p[2]) for p in self.perfis()}
            achadas = detectar_perfis(registradas) + [
                c for c in marcados if identidade_pasta(c) not in registradas]
            vistas = set()
            for pasta in achadas:
                if normalizar(pasta) in vistas:
                    continue
                vistas.add(normalizar(pasta))
                marcados.setdefault(pasta, True)
                linha = tk.Frame(rol.dentro, bg=C["CARD"], highlightthickness=1, highlightbackground=C["BORDA"],
                                 cursor="hand2")
                linha.pack(fill="x", pady=3)
                caixa = tk.Label(linha, bg=C["CARD"], fg=C["ACCENT"], font=(FONTE, 14), cursor="hand2")
                caixa.pack(side="left", padx=(12, 8), pady=8)
                # O status entra antes do texto no pack para não ser cortado por caminhos longos.
                self._chip(linha, self.t("conectado" if esta_logado(pasta) else "desconectado"),
                           C["VERDE"] if esta_logado(pasta) else C["SUB"]).pack(side="right", padx=12)
                textos = tk.Frame(linha, bg=C["CARD"])
                textos.pack(side="left", fill="x", expand=True, pady=6)
                tk.Label(textos, text=os.path.basename(pasta.rstrip("\\/")), bg=C["CARD"], fg=C["TEXTO"],
                         font=(FONTE, 10, "bold"), anchor="w").pack(fill="x")
                tk.Label(textos, text=pasta, bg=C["CARD"], fg=C["SUB"], font=(FONTE, 8), anchor="w").pack(fill="x")

                def alternar(e=None, pasta=pasta, caixa=caixa):
                    marcados[pasta] = not marcados[pasta]
                    caixa.config(text="☑" if marcados[pasta] else "☐")
                caixa.config(text="☑" if marcados[pasta] else "☐")
                for w in (linha, caixa, textos, *textos.winfo_children()):
                    w.bind("<Button-1>", alternar)
            if not vistas:
                tk.Label(rol.dentro, text=self.t("nenhum_encontrado"), bg=C["PAINEL"], fg=C["SUB"],
                         font=(FONTE, 10), wraplength=480, justify="left").pack(fill="x", pady=20)

        def procurar():
            pasta = filedialog.askdirectory(parent=j, title=self.t("procurar_pasta"))
            if not pasta:
                return
            pasta = os.path.normpath(pasta)
            if not eh_perfil_claude(pasta):
                dialogo(self, self.t("erro"), self.t("pasta_invalida"))
                return
            marcados[pasta] = True
            listar()

        def importar_db():
            arq = filedialog.askopenfilename(parent=j, title=self.t("procurar_db"),
                                             filetypes=[("perfis.db", "*.db"), ("*", "*.*")])
            if not arq:
                return
            try:
                n = importar_banco(self, arq)
            except (OSError, sqlite3.Error, ValueError) as e:
                dialogo(self, self.t("erro"), str(e))
                return
            j.destroy()
            self.forcar_estado()
            dialogo(self, self.t("importar"), self.t("importados", n=n))

        def confirmar():
            escolhidas = [p for p, v in marcados.items() if v]
            registradas = {identidade_pasta(p[2]) for p in self.perfis()}
            n = 0
            with db() as con:
                for pasta in escolhidas:
                    chave = identidade_pasta(pasta)
                    if chave in registradas or not eh_perfil_claude(pasta):
                        continue
                    self._inserir(con, self.nome_livre(con, os.path.basename(pasta.rstrip("\\/"))), pasta)
                    registradas.add(chave)
                    n += 1
            log(f"{n} perfil(is) importado(s)")
            j.destroy()
            self.forcar_estado()
            if n:
                dialogo(self, self.t("importar"), self.t("importados", n=n))

        Botao(rodape, self.t("importar_sel"), confirmar, tipo="primario").pack(side="right")
        Botao(rodape, self.t("procurar_pasta"), procurar).pack(side="left")
        Botao(rodape, self.t("procurar_db"), importar_db, tipo="fantasma").pack(side="left", padx=6)
        listar()
        j.mostrar()

    # ----- zona de perigo -----
    def apagar_tudo(self):
        palavra = self.t("palavra")
        msg = self.t("pastas_preservadas") + "\n\n" + self.t("apagar_confirma", p=palavra)
        resp = dialogo(self, self.t("apagar_tudo"), msg, entrada="",
                       botoes=[(self.t("apagar_tudo"), True, "primario"), (self.t("cancelar"), None, "normal")])
        if (resp or "").strip().upper() != palavra.upper():
            return
        self.cancelar_login()
        try:
            perfis = self.perfis()
            for perfil in perfis:
                identidade = identidade_para_apagar(perfil, perfis)
                if identidade is not None:
                    apagar_pasta_perfil(perfil, self.perfis, identidade)
            if caminho_sem_desvios(FOTOS):
                try:
                    fotos = os.listdir(FOTOS)
                except FileNotFoundError:
                    fotos = []
                for nome in fotos:
                    if nome.startswith("usuario_") and nome.lower().endswith(EXT_IMAGEM):
                        os.remove(os.path.join(FOTOS, nome))
            # O banco é o último arquivo removido para permitir nova tentativa.
            for arq in (LOG, LOG + ".old", DB):
                try:
                    os.remove(arq)
                except FileNotFoundError:
                    pass
        except (OSError, sqlite3.Error) as e:
            dialogo(self, self.t("erro"), str(e))
            self.forcar_estado()
            return
        reiniciar(self)

    def mudar_idioma(self, cod):
        self.lang = cod
        cfg_set("idioma", cod)
        self.montar()

    def mudar_tema(self, cod):
        self.tema = cod
        cfg_set("tema", cod)
        self.tema_efetivo = aplicar_tema(cod)
        _cache_img.clear()
        self.montar()

    # ----- perfis -----
    def _inserir(self, con, nome, pasta=None, foto=None):
        """Cadastra um perfil; sem `pasta`, cria uma nova em dados/ (portátil). Devolve o id."""
        if pasta is None:
            slug = re.sub(r"[^\w-]+", "_", nome).strip("_")[:80] or "perfil"
            if _DISPOSITIVO.match(slug):
                slug = "_" + slug
            pasta = os.path.join(DADOS, slug)
            usadas = {normalizar(caminho_real(r[0])) for r in con.execute("SELECT pasta FROM perfis")}
            sufixo = 2
            while normalizar(pasta) in usadas or os.path.lexists(pasta):
                pasta = os.path.join(DADOS, f"{slug}_{sufixo}")
                sufixo += 1
            validar_pasta_para_abrir(pasta)
        cur = con.execute("INSERT INTO perfis (nome, pasta, foto) VALUES (?, ?, ?)",
                          (nome, caminho_salvo(pasta), foto))
        return cur.lastrowid

    def nome_livre(self, con, nome):
        base, n = nome, 2
        while con.execute("SELECT 1 FROM perfis WHERE nome = ?", (nome,)).fetchone():
            nome, n = f"{base} ({n})", n + 1
        return nome

    def perfis(self):
        with db() as con:
            linhas = con.execute("SELECT id, nome, pasta, foto FROM perfis ORDER BY id").fetchall()
        return [(i, n, caminho_real(p), f) for i, n, p, f in linhas]

    def calcular_estado(self):
        perfis = self.perfis()
        if not perfis:
            return {}
        abertos = claudes_abertos()
        return {p[2]: (esta_logado(p[2]), normalizar(p[2]) in abertos) for p in perfis}

    def atualizar_estado(self):
        try:
            if self.tema == "sistema" and tema_sistema() != self.tema_efetivo:
                self.mudar_tema("sistema")
            novo = self.calcular_estado()
            if novo != self.estado:
                self.estado = novo
                if self.login_perfil and novo.get(self.login_perfil[2], (False, False))[0]:
                    self.cancelar_login()
                else:
                    self.recarregar()
        except Exception as e:
            log(f"erro ao atualizar estado: {e}")
        self.after(2000, self.atualizar_estado)

    def recarregar(self):
        for w in self.area.winfo_children():
            w.destroy()
        largura = self.corpo.tela.winfo_width()
        self.layout = self._layout_para(largura if largura > 1 else 600)
        colunas, compacto = self.layout
        for col in range(2):
            self.area.columnconfigure(col, weight=1 if col < colunas else 0, uniform="c" if col < colunas else "")
        perfis = self.perfis()
        if not perfis:
            vazio = tk.Frame(self.area, bg=C["BG"])
            vazio.grid(row=0, column=0, columnspan=colunas, pady=60)
            tk.Label(vazio, text="✳", bg=C["BG"], fg=C["ACCENT"], font=(FONTE, 40)).pack()
            tk.Label(vazio, text=self.t("nenhum"), bg=C["BG"], fg=C["TEXTO"], font=(FONTE, 13, "bold")).pack()
            tk.Label(vazio, text=self.t("nenhum_sub"), bg=C["BG"], fg=C["SUB"], font=(FONTE, 10)).pack(pady=(2, 14))
            Botao(vazio, "+  " + self.t("novo"), self.novo, tipo="primario").pack(fill="x")
            Botao(vazio, "⇩  " + self.t("importar"), self.importar, tipo="fantasma").pack(fill="x", pady=(6, 0))
        for i, p in enumerate(perfis):
            card = self._card(p, i, compacto)
            col = i % colunas
            card.grid(row=i // colunas, column=col, sticky="nsew", pady=6,
                      padx=(0, 6) if colunas == 2 and col == 0 else (6, 0) if colunas == 2 else 0)

    def _chip(self, master, texto, cor):
        f = tk.Frame(master, bg=C["CHIP"])
        tk.Label(f, text="●", bg=C["CHIP"], fg=cor, font=(FONTE, 8)).pack(side="left", padx=(8, 4), pady=2)
        tk.Label(f, text=texto, bg=C["CHIP"], fg=C["TEXTO"], font=(FONTE, 9)).pack(side="left", padx=(0, 9), pady=2)
        return f

    def _card(self, p, i, compacto):
        _, nome, pasta, foto = p
        logado, aberto = self.estado.get(pasta, (False, False))
        esperando = bool(self.login_perfil and self.login_perfil[2] == pasta)

        c = tk.Frame(self.area, bg=C["CARD"], highlightthickness=1,
                     highlightbackground=C["ACCENT"] if esperando else C["BORDA"])
        c.columnconfigure(1, weight=1)

        av = tk.Canvas(c, width=52, height=52, bg=C["CARD"], highlightthickness=0, cursor="hand2")
        desenhar_avatar(av, nome, foto, 52, C["CARD"], i)
        av.grid(row=0, column=0, rowspan=2, padx=(16, 12), pady=14)
        av.bind("<Button-1>", lambda e: self.escolher_foto(p))

        titulo = tk.Label(c, text=nome, bg=C["CARD"], fg=C["TEXTO"], font=(FONTE, 12, "bold"),
                          cursor="xterm", anchor="w")
        titulo.grid(row=0, column=1, sticky="sw", pady=(14, 0))
        titulo.bind("<Double-Button-1>", lambda e: self.renomear(p))
        chips = tk.Frame(c, bg=C["CARD"])
        chips.grid(row=1, column=1, sticky="nw", pady=(4, 14))
        if esperando:
            self._chip(chips, self.t("aguard"), C["AMARELO"]).pack(side="left")
        else:
            self._chip(chips, self.t("conectado" if logado else "desconectado"),
                       C["VERDE"] if logado else C["SUB"]).pack(side="left", padx=(0, 6))
            self._chip(chips, self.t("aberto" if aberto else "fechado"),
                       C["VERDE"] if aberto else C["SUB"]).pack(side="left")

        botoes = tk.Frame(c, bg=C["CARD"])
        if compacto:
            botoes.grid(row=2, column=0, columnspan=3, sticky="ew", padx=16, pady=(0, 14))
        else:
            botoes.grid(row=0, column=2, rowspan=2, padx=(8, 14))
        mais = Botao(botoes, "⋯", lambda: self.menu(p, mais, logado), tipo="fantasma", tamanho=12, padx=10, pady=3)
        mais.pack(side="right")
        acoes = []
        if not logado:
            acoes.append((self.t("entrar"), lambda: self.login(p), "primario"))
            if aberto:
                acoes.append((self.t("fechar"), lambda: self.fechar_perfil(p), "normal"))
        elif aberto:
            acoes.append((self.t("fechar"), lambda: self.fechar_perfil(p), "normal"))
        else:
            acoes.append((self.t("abrir"), lambda: self.abrir(p), "primario"))
        for texto, cmd, tipo in acoes:
            Botao(botoes, texto, cmd, tipo=tipo).pack(side="left" if compacto else "right",
                                                       padx=(0, 6) if compacto else (0, 6))
        return c

    def menu(self, p, widget, logado):
        m = tk.Menu(self, tearoff=0, bg=C["MENU"], fg=C["TEXTO"], activebackground=C["ACCENT"],
                    activeforeground="#ffffff", font=(FONTE, 10), bd=0, relief="flat")
        m.add_command(label="  " + self.t("foto"), command=lambda: self.escolher_foto(p))
        m.add_command(label="  " + self.t("renomear"), command=lambda: self.renomear(p))
        m.add_command(label="  " + self.t("pasta"), command=lambda: self.pasta(p))
        if logado:
            m.add_command(label="  " + self.t("sair"), command=lambda: self.sair(p))
        m.add_separator()
        m.add_command(label="  " + self.t("excluir"), command=lambda: self.excluir(p), foreground=C["VERMELHO"])
        m.tk_popup(widget.winfo_rootx(), widget.winfo_rooty() + widget.winfo_height())

    def precisa_claude(self):
        if not self.claude:
            dialogo(self, self.t("erro"), self.t("ausente"))
        return bool(self.claude)

    def abrir(self, p):
        if self.precisa_claude():
            try:
                abrir_claude(self.claude, p[2])
            except Exception as e:
                dialogo(self, self.t("erro"), str(e))
            self.after(1500, self.forcar_estado)

    def fechar_perfil(self, p):
        if self.login_perfil and self.login_perfil[0] == p[0]:
            self.cancelar_login()
        try:
            fechar_claude(p[2])
        except OSError as e:
            dialogo(self, self.t("erro"), str(e))
        self.forcar_estado()

    def forcar_estado(self):
        self.estado = self.calcular_estado()
        self.recarregar()

    def confirmar(self, titulo, msg, perigo=False):
        return dialogo(self, titulo, msg, botoes=[(self.t("sim"), True, "primario"),
                                                  (self.t("nao"), False, "normal")]) is True

    def sair(self, p):
        if not self.confirmar(self.t("sair"), self.t("sair_q", n=p[1])):
            return
        if self.login_perfil and self.login_perfil[0] == p[0]:
            self.cancelar_login()
        try:
            if not eh_perfil_claude(p[2]):
                raise ValueError(self.t("pasta_invalida"))
            fechar_claude(p[2])
            deslogar(p[2])
        except (OSError, ValueError) as e:
            dialogo(self, self.t("erro"), str(e))
            return
        log(f"deslogado: {p[1]}")
        self.forcar_estado()
        self.mostrar_status(self.t("saiu", n=p[1]), False)
        self.after(5000, self.esconder_status)

    # ----- fotos -----
    def escolher_foto(self, p):
        j = Janela(self, self.t("fotos_titulo", n=p[1]), largura=520, altura=520)
        tk.Label(j, text=self.t("fotos_dica"), bg=C["PAINEL"], fg=C["SUB"], font=(FONTE, 9),
                 anchor="w", justify="left", wraplength=460).pack(fill="x", padx=20, pady=(16, 6))
        rodape = tk.Frame(j, bg=C["PAINEL"])
        rodape.pack(side="bottom", fill="x", padx=20, pady=14)
        rol = Rolavel(j, C["PAINEL"])
        rol.pack(fill="both", expand=True, padx=14)
        grade = rol.dentro
        estado = {"colunas": 0}

        def definir(foto):
            with db() as con:
                con.execute("UPDATE perfis SET foto = ? WHERE id = ?", (foto, p[0]))
            j.destroy()
            self.recarregar()

        def desenhar(largura):
            colunas = max(3, (largura - 8) // 96)
            if colunas == estado["colunas"]:
                return
            estado["colunas"] = colunas
            for w in grade.winfo_children():
                w.destroy()
            for col in range(colunas):
                grade.columnconfigure(col, weight=1)
            for k, nome in enumerate(listar_fotos()):
                ativo = nome == p[3]
                cv = tk.Canvas(grade, width=80, height=80, bg=C["PAINEL"], cursor="hand2",
                               highlightthickness=3, highlightbackground=C["ACCENT"] if ativo else C["PAINEL"])
                img = avatar(nome, 76, C["PAINEL"])
                if not img:
                    cv.destroy()
                    continue
                cv.create_image(40, 40, image=img)
                cv.imagem = img
                cv.grid(row=k // colunas, column=k % colunas, padx=4, pady=4)
                cv.bind("<Button-1>", lambda e, nome=nome: definir(nome))

        rol.tela.bind("<Configure>", lambda e: desenhar(e.width), add="+")

        def adicionar():
            arq = filedialog.askopenfilename(parent=j, title=self.t("adicionar_img"), filetypes=[
                (self.t("imagens"), " ".join("*" + e for e in EXT_IMAGEM)), ("*", "*.*")])
            if not arq:
                return
            try:
                definir(importar_foto(arq))
            except Exception as e:
                log(f"erro ao importar foto: {e}")
                dialogo(self, self.t("erro"), self.t("img_erro"))

        if Image:
            Botao(rodape, "+  " + self.t("adicionar_img"), adicionar, tipo="primario").pack(side="left")
        Botao(rodape, self.t("abrir_fotos"), lambda: os.startfile(FOTOS)).pack(side="left", padx=6)
        Botao(rodape, self.t("sem_foto"), lambda: definir(None), tipo="fantasma").pack(side="right")
        j.mostrar()

    # ----- login -----
    def login(self, p):
        if not self.precisa_claude():
            return
        try:
            abrir_claude(self.claude, p[2])
        except (OSError, ValueError) as e:
            dialogo(self, self.t("erro"), str(e))
            return
        self.cancelar_login()
        self.login_perfil = p
        self.login_timer = self.after(LOGIN_TIMEOUT * 1000, lambda: self.cancelar_login(self.t("esgotado")))
        self.mostrar_status(self.t("aguardando", n=p[1]), True)
        self.recarregar()

    def colar_link(self):
        perfil = self.login_perfil
        tentativa = self.login_timer
        if perfil is None:
            return
        url = dialogo(self, f"{self.t('colar')} · {perfil[1]}", self.t("colar_pergunta"),
                      entrada="", ocultar=True, botoes=[(self.t("ok"), True, "primario"),
                                                       (self.t("cancelar"), None, "normal")])
        if not url or self.login_perfil is not perfil or self.login_timer != tentativa:
            return
        rota = r"claude://(?:login/|claude\.ai/(?:magic-link|sso-callback|login)[/?#])([^\s\x00-\x1f]+)"
        retorno = re.fullmatch(rota, url)
        if not retorno or not retorno[1].strip("/?#&="):
            dialogo(self, self.t("erro"), self.t("invalido"))
            return
        try:
            abrir_claude(self.claude, perfil[2], url)
        except (OSError, ValueError) as e:
            dialogo(self, self.t("erro"), e.strerror if isinstance(e, OSError) and e.strerror else self.t("pasta_invalida"))
            return
        self.cancelar_login()
        self.mostrar_status(self.t("enviado", n=perfil[1]), False)
        self.after(6000, self.esconder_status)
        self.after(3000, self.forcar_estado)

    def cancelar_login(self, msg=None):
        self.login_perfil = None
        if self.login_timer is not None:
            self.after_cancel(self.login_timer)
            self.login_timer = None
        if msg:
            self.mostrar_status(msg, False)
            self.after(6000, self.esconder_status)
        else:
            self.esconder_status()
        self.recarregar()

    def mostrar_status(self, texto, cancelavel):
        self.status_txt.config(text=texto)
        self.status_btns.pack_forget()
        if cancelavel:
            self.status_btns.pack(side="right", padx=10, pady=8)
        self.status.pack(fill="x", padx=22, pady=(14, 0), before=self.corpo)

    def esconder_status(self):
        if not self.login_perfil:
            self.status.pack_forget()

    # ----- outras ações -----
    def novo(self):
        nome = dialogo(self, self.t("novo"), self.t("novo_pergunta"), entrada="",
                       botoes=[(self.t("ok"), True, "primario"), (self.t("cancelar"), None, "normal")])
        if not nome:
            return
        try:
            with db() as con:
                novo_id = self._inserir(con, nome)
        except sqlite3.IntegrityError:
            dialogo(self, self.t("erro"), self.t("existe"))
            return
        except OSError as e:
            dialogo(self, self.t("erro"), str(e))
            return
        self.forcar_estado()
        # Logo depois de criar, já pede a foto (galeria ou upload).
        perfil = next((p for p in self.perfis() if p[0] == novo_id), None)
        if perfil:
            self.escolher_foto(perfil)

    def renomear(self, p):
        nome = dialogo(self, self.t("renomear"), self.t("renomear_pergunta"), entrada=p[1],
                       botoes=[(self.t("ok"), True, "primario"), (self.t("cancelar"), None, "normal")])
        if not nome:
            return
        try:
            with db() as con:
                con.execute("UPDATE perfis SET nome = ? WHERE id = ?", (nome, p[0]))
        except sqlite3.IntegrityError:
            dialogo(self, self.t("erro"), self.t("existe"))
        self.recarregar()

    def pasta(self, p):
        try:
            validar_pasta_para_abrir(p[2])
            os.makedirs(p[2], exist_ok=True)
            os.startfile(p[2])
        except OSError as e:
            dialogo(self, self.t("erro"), str(e))

    def excluir(self, p):
        try:
            perfis = self.perfis()
            if p not in perfis:
                return
            identidade = identidade_para_apagar(p, perfis)
            msg = self.t("excluir_q", n=p[1])
            if identidade is None:
                msg += "\n\n" + self.t("pastas_preservadas")
            if not self.confirmar(self.t("excluir"), msg):
                return
            apagar = identidade is not None and self.confirmar(self.t("excluir"), self.t("excluir_dados"))
            if self.login_perfil and self.login_perfil[0] == p[0]:
                self.cancelar_login()
            if apagar:
                apagar_pasta_perfil(p, self.perfis, identidade)
            with db() as con:
                atual = con.execute("SELECT pasta FROM perfis WHERE id = ?", (p[0],)).fetchone()
                if atual and normalizar(caminho_real(atual[0])) == normalizar(p[2]):
                    con.execute("DELETE FROM perfis WHERE id = ? AND pasta IS ?", (p[0], atual[0]))
                    con.execute("DELETE FROM config WHERE chave = ?", (f"apagar_perfil_{p[0]}",))
        except (OSError, sqlite3.Error) as e:
            dialogo(self, self.t("erro"), str(e))
        self.forcar_estado()

    def fechar(self):
        if self.state() == "normal":
            cfg_set("geometria", self.geometry())
        self.destroy()


_MUTEX = None


def reiniciar(app):
    """Fecha e abre o programa de novo (usado depois de apagar todos os dados)."""
    app.destroy()
    if _MUTEX:
        _k32.CloseHandle(wt.HANDLE(_MUTEX))
    args = [sys.executable] if CONGELADO else [sys.executable, os.path.abspath(__file__)]
    subprocess.Popen(args)
    sys.exit()


def principal():
    global _MUTEX
    if sys.argv[1:2] in (["--link"], ["--browser"]):
        ctypes.windll.user32.MessageBoxW(None, "In Windows Settings → Apps → Default apps, "
                                        "select your browser for web links and Claude for claude://.", APP, 0x40)
        return
    ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("RichardKPL.MultiClaude")
    _k32.CreateMutexW.restype = wt.HANDLE
    # Uma instância por pasta: cópias portáteis em pastas diferentes podem rodar juntas.
    _MUTEX = _k32.CreateMutexW(None, False, "MultiClaude_" + hashlib.md5(normalizar(BASE).encode()).hexdigest()[:16])
    if ctypes.get_last_error() == 183:  # ERROR_ALREADY_EXISTS
        ctypes.windll.user32.MessageBoxW(None, f"{APP} já está aberto.", APP, 0x40)
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)  # nitidez em telas com escala
    except Exception:
        pass
    log(f"{APP} {VERSAO} aberto")
    app = App()
    app.report_callback_exception = lambda *e: log("ERRO: " + "".join(__import__("traceback").format_exception(*e)))
    app.mainloop()


if __name__ == "__main__":
    principal()
