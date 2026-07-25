window.palCharts = {
  _charts: {},
  // Pull chart colors from the CSS design tokens so charts match the active (light/dark) theme.
  _token(name, fallback) {
    const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return v || fallback;
  },
  _themed(seriesLabel) {
    const accent = this._token('--accent', '#5b93ff');
    const grid = this._token('--border', '#2e323c');
    const text = this._token('--text-dim', '#9aa0aa');
    return { accent, grid, text };
  },
  renderLineChart(id, labels, series, seriesLabel) {
    const ctx = document.getElementById(id);
    if (!ctx) return;
    this._charts[id]?.destroy();
    const t = this._themed(seriesLabel);
    this._charts[id] = new Chart(ctx, {
      type: 'line',
      data: {
        labels,
        datasets: [{
          label: seriesLabel, data: series, tension: 0.3, pointRadius: 0,
          borderColor: t.accent, backgroundColor: t.accent + '22', fill: true, borderWidth: 2
        }]
      },
      options: {
        animation: false, responsive: true, maintainAspectRatio: false,
        plugins: { legend: { labels: { color: t.text } } },
        scales: {
          y: { beginAtZero: true, grid: { color: t.grid }, ticks: { color: t.text } },
          x: { grid: { color: t.grid }, ticks: { color: t.text } }
        }
      }
    });
  },
  updateLineChart(id, labels, series) {
    const c = this._charts[id];
    if (!c) return;
    c.data.labels = labels; c.data.datasets[0].data = series; c.update('none');
  }
};
