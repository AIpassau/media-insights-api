"""Regression tests for the offline administrator setup command."""
import os
from pathlib import Path
import secrets
import sqlite3
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory() as temp:
    database = str(Path(temp) / 'setup.db')
    env = dict(os.environ, Database__Provider="Sqlite", DATABASE_URL="", ConnectionStrings__AppDb=f'Data Source={database}')
    def run(lines):
        result = subprocess.run(['dotnet', str(root / 'bin/Debug/net10.0/api.dll'), '--setup-admin'],
                                cwd=root, env=env, input='\n'.join(lines) + '\n', text=True, capture_output=True)
        return result.returncode
    password = secrets.token_urlsafe(24) + 'Aa1!'
    assert run(['invalid']) == 1
    assert run(['admin@example.test', password, 'different']) == 1
    assert run(['admin@example.test', 'weak', 'weak']) == 1
    assert run(['admin@example.test', password, password]) == 0
    with sqlite3.connect(database) as db:
        uid, old_hash = db.execute('SELECT Id, PasswordHash FROM AspNetUsers').fetchone()
        assert old_hash != password
        assert db.execute('SELECT COUNT(*) FROM AspNetUserRoles').fetchone()[0] == 1
    assert run(['admin@example.test', 'CANCEL']) == 1
    new_password = secrets.token_urlsafe(24) + 'Bb2!'
    assert run(['admin@example.test', 'RESET', new_password, new_password]) == 0
    with sqlite3.connect(database) as db:
        assert db.execute('SELECT COUNT(*) FROM AspNetUsers').fetchone()[0] == 1
        assert db.execute('SELECT PasswordHash FROM AspNetUsers').fetchone()[0] != old_hash
        db.execute('DELETE FROM AspNetUserRoles')
    assert run(['admin@example.test']) == 1
    print('PASS: validation, password confirmation, creation, stored hash/role, cancellation, reset, no ordinary-user promotion')
