"""Run after `dotnet build api`: isolated SQLite/HTTP authorization regression test."""
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]

with tempfile.TemporaryDirectory() as temp:
    database = str(Path(temp) / 'test.db')
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    password = secrets.token_urlsafe(24) + 'Aa1!'
    env = dict(os.environ, Database__Provider="Sqlite", DATABASE_URL="", ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=base,
               ConnectionStrings__AppDb=f'Data Source={database}',
               Admin__Email='admin@example.test', Admin__Password=password)
    log = open(Path(temp) / 'server.log', 'w+')
    server = subprocess.Popen(['dotnet', str(ROOT / 'bin/Debug/net10.0/api.dll')], cwd=ROOT,
                              env=env, stdout=log, stderr=log)
    def client():
        return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    def call(browser, path, method='GET', data=None, csrf=True):
        headers = {}
        if method != 'GET' and csrf:
            _, token = call(browser, '/api/auth/csrf')
            headers['X-XSRF-TOKEN'] = token['token']
        if data is not None:
            headers['Content-Type'] = 'application/json'
        request = urllib.request.Request(base + path, method=method, headers=headers,
                                         data=None if data is None else json.dumps(data).encode())
        try:
            response = browser.open(request)
        except urllib.error.HTTPError as error:
            response = error
        body = response.read()
        try:
            body = json.loads(body)
        except ValueError:
            pass
        return response.code, body
    try:
        guest, admin, user = client(), client(), client()
        for _ in range(100):
            if server.poll() is not None:
                log.seek(0)
                raise AssertionError(log.read())
            try:
                if call(guest, '/api/health')[0] == 200:
                    break
            except OSError:
                time.sleep(.1)
        else:
            raise AssertionError('Server did not start')
        assert call(guest, '/api/auth/login', 'POST', {'email': 'admin@example.test', 'password': password})[0] == 200
        admin = guest
        guest = client()
        assert call(user, '/api/auth/register', 'POST', {'email': 'user@example.test', 'password': password, 'admin': True})[0] == 200
        with sqlite3.connect(database) as db:
            db.execute('UPDATE AspNetUsers SET EmailConfirmed=1 WHERE Email=?', ('user@example.test',))
        assert call(user, '/api/auth/login', 'POST', {'email': 'user@example.test', 'password': password})[0] == 200
        assert call(user, '/api/auth/me')[1]['admin'] is False
        users = call(admin, '/api/auth/admin/users')[1]
        uid = next(u['id'] for u in users if not u['admin'])
        aid = next(u['id'] for u in users if u['admin'])
        programme = dict(de='Test', en='Test', medium='TV', categoryDe='Test', categoryEn='Test', daily=[1,2,3,4,5,6])
        for browser, expected in [(guest, 401), (user, 403)]:
            for path, method, body in [('/api/auth/admin/users', 'GET', None),
                (f'/api/auth/admin/users/{uid}', 'DELETE', None),
                (f'/api/auth/admin/users/{uid}/disabled', 'POST', {'disabled': True}),
                ('/api/admin/programmes', 'POST', programme),
                ('/api/admin/programmes/1', 'PUT', programme),
                ('/api/admin/programmes/1', 'DELETE', None)]:
                assert call(browser, path, method, body)[0] == expected, (path, expected)
        assert call(admin, '/api/admin/programmes', 'POST', programme, csrf=False)[0] == 400
        assert call(admin, '/api/admin/programmes', 'POST', dict(programme, daily=[-1]))[0] == 400
        status, created = call(admin, '/api/admin/programmes', 'POST', programme)
        assert status == 200
        mid = created['id']
        assert call(admin, f'/api/admin/programmes/{mid}', 'PUT', dict(programme, en='Changed'))[0] == 200
        assert any(p['en'] == 'Changed' for p in call(guest, '/api/programmes')[1])
        with sqlite3.connect(database) as db:
            assert db.execute('SELECT En FROM Programmes WHERE Id=?', (mid,)).fetchone()[0] == 'Changed'
        assert call(admin, f'/api/admin/programmes/{mid}', 'DELETE')[0] == 200
        assert not any(p['id'] == mid for p in call(guest, '/api/programmes')[1])
        assert call(admin, f'/api/auth/admin/users/{aid}', 'DELETE')[0] == 400
        assert call(admin, f'/api/auth/admin/users/{aid}/disabled', 'POST', {'disabled': True})[0] == 400
        assert call(admin, f'/api/auth/admin/users/{uid}/disabled', 'POST', {'disabled': True})[0] == 200
        assert call(user, '/api/auth/me')[0] == 401
        assert call(admin, f'/api/auth/admin/users/{uid}/disabled', 'POST', {'disabled': False})[0] == 200
        assert call(user, '/api/auth/me')[0] == 401
        assert call(user, '/api/auth/login', 'POST', {'email': 'user@example.test', 'password': password})[0] == 200
        assert call(admin, f'/api/auth/admin/users/{uid}', 'DELETE')[0] == 200
        assert call(user, '/api/auth/me')[0] == 401
        with sqlite3.connect(database) as db:
            db.execute('DELETE FROM AspNetUserRoles WHERE UserId=?', (aid,))
        assert call(admin, '/api/auth/admin/users')[0] == 403
        print('PASS: guest/user authorization, CSRF, validation, media CRUD persistence, admin protection, disabled/deleted sessions, live role revocation')
    finally:
        server.terminate()
        server.wait(timeout=10)
        log.close()
