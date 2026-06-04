:root {
  --bg: #0b1220;
  --panel: #131d31;
  --panel-2: #1a2742;
  --text: #e8edf7;
  --muted: #aab6cc;
  --accent: #4ea1ff;
  --accent-2: #2d79d6;
  --border: rgba(255,255,255,0.08);
  --shadow: 0 12px 30px rgba(0,0,0,0.28);
  --radius: 18px;
}

* { box-sizing: border-box; }

html, body {
  margin: 0;
  padding: 0;
  background: linear-gradient(180deg, #09101d 0%, #0d1728 100%);
  color: var(--text);
  font-family: Segoe UI, Arial, sans-serif;
}

a { text-decoration: none; color: inherit; }

.site-header,
main,
.site-footer {
  width: min(1200px, calc(100% - 32px));
  margin: 0 auto;
}

.site-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 24px 0;
  gap: 20px;
}

.brand-wrap {
  display: flex;
  align-items: center;
  gap: 16px;
}

.brand-logo {
  width: 64px;
  height: 64px;
  object-fit: contain;
}

.brand-title {
  margin: 0;
  font-size: 28px;
}

.brand-subtitle {
  margin: 6px 0 0;
  color: var(--muted);
}

.hero {
  background: linear-gradient(145deg, rgba(78,161,255,0.12), rgba(255,255,255,0.03));
  border: 1px solid var(--border);
  border-radius: 24px;
  box-shadow: var(--shadow);
  padding: 42px;
  margin-top: 18px;
}

.hero h2 {
  margin-top: 0;
  font-size: 40px;
}

.hero p {
  color: var(--muted);
  font-size: 18px;
  line-height: 1.6;
  max-width: 900px;
}

.hero-actions,
.header-actions {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
}

.info-grid {
  margin-top: 28px;
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 18px;
}

.card {
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 22px;
  box-shadow: var(--shadow);
}

.card h3 {
  margin-top: 0;
  font-size: 20px;
}

.card p {
  color: var(--muted);
  line-height: 1.6;
}

.btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 44px;
  padding: 0 18px;
  border-radius: 12px;
  font-weight: 700;
  cursor: pointer;
  transition: 0.18s ease;
}

.btn-primary {
  background: var(--accent);
  color: white;
}

.btn-primary:hover {
  background: var(--accent-2);
}

.btn-secondary {
  background: rgba(255,255,255,0.06);
  border: 1px solid var(--border);
  color: var(--text);
}

.btn-secondary:hover {
  background: rgba(255,255,255,0.10);
}

.site-footer {
  padding: 28px 0 40px;
  color: var(--muted);
  text-align: center;
}

.selector-wrap {
  width: min(900px, calc(100% - 32px));
  margin: 40px auto;
}

.selector-list {
  display: grid;
  gap: 16px;
  margin-top: 20px;
}

.vtc-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: 18px;
  padding: 18px;
  gap: 16px;
}

.vtc-meta {
  display: flex;
  align-items: center;
  gap: 14px;
}

.vtc-logo {
  width: 56px;
  height: 56px;
  border-radius: 14px;
  object-fit: cover;
  background: rgba(255,255,255,0.04);
}

.vtc-name {
  margin: 0;
  font-size: 20px;
}

.vtc-role {
  margin: 6px 0 0;
  color: var(--muted);
}

.status-box {
  margin-top: 18px;
  padding: 18px;
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: 16px;
  color: var(--muted);
}
