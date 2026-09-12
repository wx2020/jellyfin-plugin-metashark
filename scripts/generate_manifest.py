#!/usr/bin/env python3
import hashlib
import json
import sys
import re
import os
import subprocess
from datetime import datetime
from urllib.request import urlopen
from urllib.error import HTTPError


def generate_manifest():
    return    [{
        "guid": "9a19103f-16f7-4668-be54-9a1e7a4f7556",
        "name": "MetaShark",
        "description": "jellyfin电影元数据插件，影片信息只要从豆瓣获取，并由TMDB补充缺失的剧集数据。",
        "overview": "jellyfin电影元数据插件",
        "owner": "wx2020",
        "category": "Metadata",
        "imageUrl": "https://github.com/wx2020/jellyfin-plugin-metashark/raw/main/doc/logo.png",
        "versions": []
    }]

def normalize_version(tag):
    # 三段 tag（X.Y.Z）补 .0，四段（X.Y.Z.W）保持原样，兼容 System.Version 的 2~4 段限制
    version = tag.lstrip('v')
    if len(version.split('.')) == 3:
        return f"{version}.0"
    return version

def generate_version(filepath, version, changelog):
    return {
        'version': version,
        'changelog': changelog,
        'targetAbi': '10.11.0.0',
        'sourceUrl': f'https://github.com/wx2020/jellyfin-plugin-metashark/releases/download/v{version}/metashark_{version}.zip',
        'checksum': md5sum(filepath),
        'timestamp': datetime.now().strftime('%Y-%m-%dT%H:%M:%S')
    }

def md5sum(filename):
    with open(filename, 'rb') as f:
        return hashlib.md5(f.read()).hexdigest()


def is_valid_version(version):
    # Jellyfin 后端用 System.Version.Parse 解析 manifest 的 version，只接受 2~4 段纯数字。
    # 之前 v2.3.6-fix1 拼出 2.3.6-fix1.0 导致 GET /Packages 直接 500，故在此处拦截。
    parts = str(version).split('.')
    return 2 <= len(parts) <= 4 and all(p.isdigit() for p in parts)


def main():
    filename = sys.argv[1]
    tag = sys.argv[2]
    version = normalize_version(tag)
    if not is_valid_version(version):
        raise SystemExit(f"tag {tag} 派生的 manifest version {version} 非法：必须 2~4 段纯数字（如 v2.3.7 或 v2.3.7.1），后缀只写进 changelog/Release 文案")
    filepath = os.path.join(os.getcwd(), filename)
    result = subprocess.run(['git', 'tag','-l','--format=%(contents)', tag, '-l'], stdout=subprocess.PIPE)
    changelog = result.stdout.decode('utf-8').strip()

    # 解析旧 manifest
    try:
        with urlopen('https://github.com/wx2020/jellyfin-plugin-metashark/releases/download/manifest/manifest.json') as f:
            manifest = json.load(f)
    except HTTPError as err:
        if err.code == 404:
            manifest = generate_manifest()
        else:
            raise

    # 追加新版本/覆盖旧版本，并顺手清除历史非法版本条目（如 2.3.6-fix1.0）
    manifest[0]['versions'] = list(filter(lambda x: x['version'] != version and is_valid_version(x['version']), manifest[0]['versions']))
    manifest[0]['versions'].insert(0, generate_version(filepath, version, changelog))

    with open('manifest.json', 'w') as f:
        json.dump(manifest, f, indent=2)

    # # 国内加速
    cn_domain = 'https://ghfast.top/'
    if 'CN_DOMAIN' in os.environ and os.environ["CN_DOMAIN"]:
        cn_domain = os.environ["CN_DOMAIN"]
    cn_domain = cn_domain.rstrip('/')
    with open('manifest_cn.json', 'w') as f:
        manifest_cn = json.dumps(manifest, indent=2)
        manifest_cn = re.sub('https://github.com', f'{cn_domain}/https://github.com', manifest_cn)
        f.write(manifest_cn)


if __name__ == '__main__':
    main()