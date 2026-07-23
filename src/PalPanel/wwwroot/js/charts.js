window.palCharts = {
  _charts: {},
  renderLineChart(id, labels, series, seriesLabel) {
    const ctx = document.getElementById(id);
    if (!ctx) return;
    this._charts[id]?.destroy();
    this._charts[id] = new Chart(ctx, {
      type: 'line',
      data: { labels, datasets: [{ label: seriesLabel, data: series, tension: 0.3, pointRadius: 0 }] },
      options: { animation: false, responsive: true, maintainAspectRatio: false,
                 scales: { y: { beginAtZero: true } } }
    });
  },
  updateLineChart(id, labels, series) {
    const c = this._charts[id];
    if (!c) return;
    c.data.labels = labels; c.data.datasets[0].data = series; c.update('none');
  }
};
