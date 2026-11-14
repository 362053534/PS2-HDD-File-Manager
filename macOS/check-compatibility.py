#!/usr/bin/env python3
"""打包前验证两个架构及真正的 Mach-O 最低系统版本，不改写二进制头。"""
import re
import subprocess
import sys

def version(value):
    numbers = tuple(int(part) for part in value.split('.'))
    return (numbers + (0, 0, 0))[:3]

def check(path):
    architectures = set(subprocess.check_output(['xcrun', 'lipo', '-archs', path], text=True).split())
    if architectures != {'arm64', 'x86_64'}:
        raise ValueError(f'{path}: 需要 arm64 + x86_64，实际为 {sorted(architectures)}')
    for architecture in sorted(architectures):
        text = subprocess.check_output(['xcrun', 'otool', '-arch', architecture, '-l', path], text=True)
        modern = re.search(r'\bcmd LC_BUILD_VERSION\b(?:(?!Load command).)*?\bminos ([0-9.]+)', text, re.S)
        legacy = re.search(r'\bcmd LC_VERSION_MIN_MACOSX\b(?:(?!Load command).)*?\bversion ([0-9.]+)', text, re.S)
        match = modern or legacy
        if not match or version(match.group(1)) > (12, 0, 0):
            raise ValueError(f'{path}: {architecture} 最低系统版本不兼容 macOS 12.0: {match.group(1) if match else "缺失"}')
        print(f'PASS {path}: {architecture}, minOS {match.group(1)}')

if __name__ == '__main__':
    if len(sys.argv) < 2:
        sys.exit('用法：check-compatibility.py <Mach-O> ...')
    try:
        for path in sys.argv[1:]:
            check(path)
    except (ValueError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
