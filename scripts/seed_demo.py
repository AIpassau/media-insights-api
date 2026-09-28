"""Insert fictional showcase media without replacing existing programmes or accounts."""
import json
from pathlib import Path
import sqlite3

root = Path(__file__).resolve().parents[1]
rows = json.loads((root / 'programmes.seed.json').read_text())
with sqlite3.connect(root / 'media-insights.db', timeout=20) as db:
    db.execute('''CREATE TABLE IF NOT EXISTS Programmes (
        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, De TEXT NOT NULL, En TEXT NOT NULL,
        Medium TEXT NOT NULL, CategoryDe TEXT NOT NULL, CategoryEn TEXT NOT NULL, Daily TEXT NOT NULL)''')
    db.execute('CREATE TABLE IF NOT EXISTS ContentInitialization (Id INTEGER PRIMARY KEY)')
    inserted = 0
    for row in rows:
        if db.execute('SELECT 1 FROM Programmes WHERE De=? AND En=?', (row['de'], row['en'])).fetchone():
            continue
        db.execute('INSERT INTO Programmes (De,En,Medium,CategoryDe,CategoryEn,Daily) VALUES (?,?,?,?,?,?)',
                   (row['de'], row['en'], row['medium'], row['categoryDe'], row['categoryEn'], json.dumps(row['daily'])))
        inserted += 1
    db.execute('INSERT OR IGNORE INTO ContentInitialization (Id) VALUES (1)')
    print(f'Inserted {inserted} fictional programmes.')
    print('Totals by medium:', db.execute('SELECT Medium, COUNT(*) FROM Programmes GROUP BY Medium').fetchall())
