"""Read MSI metadata with the native Windows Installer API; no installation."""
import ctypes as c
from ctypes import wintypes as w
import json
import sys
from pathlib import Path

msi = c.WinDLL('msi')
handle = w.UINT
msi.MsiOpenDatabaseW.argtypes = [w.LPCWSTR, w.LPCWSTR, c.POINTER(handle)]
msi.MsiDatabaseOpenViewW.argtypes = [handle, w.LPCWSTR, c.POINTER(handle)]
msi.MsiViewExecute.argtypes = [handle, handle]
msi.MsiViewFetch.argtypes = [handle, c.POINTER(handle)]
msi.MsiRecordGetStringW.argtypes = [handle, w.UINT, w.LPWSTR, c.POINTER(w.DWORD)]
msi.MsiRecordGetFieldCount.argtypes = [handle]
msi.MsiCloseHandle.argtypes = [handle]
msi.MsiRecordReadStream.argtypes = [handle, w.UINT, c.c_void_p, c.POINTER(w.DWORD)]

def check(code):
    if code:
        raise OSError(code, 'Windows Installer API failed')

def query(db, sql):
    view = handle()
    check(msi.MsiDatabaseOpenViewW(db, sql, c.byref(view)))
    rows = []
    try:
        check(msi.MsiViewExecute(view, 0))
        while True:
            record = handle()
            code = msi.MsiViewFetch(view, c.byref(record))
            if code == 259:
                break
            check(code)
            try:
                values = []
                for index in range(1, msi.MsiRecordGetFieldCount(record) + 1):
                    size = w.DWORD(65535)
                    text = c.create_unicode_buffer(size.value)
                    check(msi.MsiRecordGetStringW(record, index, text, c.byref(size)))
                    values.append(text.value)
                rows.append(values)
            finally:
                msi.MsiCloseHandle(record)
    finally:
        msi.MsiCloseHandle(view)
    return rows

db = handle()
check(msi.MsiOpenDatabaseW(sys.argv[1], None, c.byref(db)))
try:
    props = dict(query(db, 'SELECT `Property`, `Value` FROM `Property`'))
    tables = ('Dialog', 'File', 'Component', 'Directory', 'Media', 'Shortcut', 'Upgrade', 'InstallExecuteSequence', 'Wix4CloseApplication')
    present = {row[0] for row in query(db, 'SELECT `Name` FROM `_Tables`')}
    data = {name: query(db, f'SELECT * FROM `{name}`') if name in present else [] for name in tables}
    data['Property'] = {key: props.get(key) for key in (
        'ProductName', 'ProductVersion', 'ProductLanguage', 'ProductCode',
        'UpgradeCode', 'WixUIRMOption', 'WIXUI_INSTALLDIR', 'ARPPRODUCTICON')}
    data['Counts'] = {name: len(data[name]) for name in tables}
    if len(sys.argv) == 3:
        destination = Path(sys.argv[2])
        destination.mkdir(parents=True, exist_ok=False)
        for media in data['Media']:
            cabinet = media[3]
            if not cabinet.startswith('#'):
                raise ValueError('Expected embedded cabinet')
            view, record = handle(), handle()
            check(msi.MsiDatabaseOpenViewW(db, f"SELECT `Data` FROM `_Streams` WHERE `Name` = '{cabinet[1:]}'", c.byref(view)))
            try:
                check(msi.MsiViewExecute(view, 0))
                check(msi.MsiViewFetch(view, c.byref(record)))
                with (destination / cabinet[1:]).open('xb') as output:
                    while True:
                        buf = c.create_string_buffer(65536)
                        size = w.DWORD(65536)
                        check(msi.MsiRecordReadStream(record, 1, buf, c.byref(size)))
                        if not size.value:
                            break
                        output.write(buf.raw[:size.value])
            finally:
                if record.value:
                    msi.MsiCloseHandle(record)
                msi.MsiCloseHandle(view)
    print(json.dumps(data, ensure_ascii=False, indent=2))
finally:
    msi.MsiCloseHandle(db)
