const charts = new Map();

const chartTargets = {
    usage: "metrics-usage-chart",
    tokenMix: "metrics-token-mix-chart",
    distribution: "metrics-distribution-chart"
};

export function renderMetricsDashboardCharts(payload) {
    if (typeof window === "undefined" || !window.ApexCharts || !payload) {
        return;
    }

    renderOrUpdate("usage", buildUsageOptions(payload));
    renderOrUpdate("tokenMix", buildTokenMixOptions(payload));
    renderOrUpdate("distribution", buildDistributionOptions(payload));
}

export function disposeMetricsDashboardCharts() {
    for (const chart of charts.values()) {
        try {
            chart.destroy();
        } catch {
            // ApexCharts may already have detached during Blazor disposal.
        }
    }

    charts.clear();
}

function renderOrUpdate(name, options) {
    const element = document.getElementById(chartTargets[name]);
    if (!element) {
        destroyChart(name);
        return;
    }

    const existingChart = charts.get(name);
    if (existingChart) {
        try {
            existingChart.updateOptions(options, true, true);
        } catch {
            destroyChart(name);
        }

        return;
    }

    try {
        const chart = new window.ApexCharts(element, options);
        charts.set(name, chart);
        chart.render();
    } catch {
        destroyChart(name);
    }
}

function destroyChart(name) {
    const chart = charts.get(name);
    if (!chart) {
        return;
    }

    try {
        chart.destroy();
    } catch {
        // ApexCharts may already have detached during Blazor disposal.
    }

    charts.delete(name);
}

function buildUsageOptions(payload) {
    const labels = readStringArray(payload.labels);

    return withBaseOptions({
        chart: {
            type: "line",
            height: 320,
            toolbar: { show: false },
            background: "transparent"
        },
        series: [
            {
                name: readNamedValues(payload.tokenMix)[0]?.name || "Input",
                data: readNumberArray(payload.inputTokens, labels.length)
            },
            {
                name: readNamedValues(payload.tokenMix)[1]?.name || "Output",
                data: readNumberArray(payload.outputTokens, labels.length)
            },
            {
                name: typeof payload.egressName === "string" ? payload.egressName : "Egress",
                data: readNumberArray(payload.egress, labels.length)
            }
        ],
        stroke: {
            curve: "smooth",
            width: 3
        },
        xaxis: { categories: labels },
        yaxis: [
            { min: 0 },
            { min: 0, show: false },
            { min: 0, opposite: true }
        ],
        markers: { size: 0 },
        tooltip: { shared: true },
        colors: ["#167bff", "#6c74f8", "#4caf50"]
    });
}

function buildTokenMixOptions(payload) {
    const tokenMix = readNamedValues(payload.tokenMix);

    return withBaseOptions({
        chart: {
            type: "donut",
            height: 280,
            parentHeightOffset: 0,
            toolbar: { show: false },
            background: "transparent"
        },
        series: tokenMix.map((item) => item.value),
        labels: tokenMix.map((item) => item.name),
        legend: {
            position: "top",
            horizontalAlign: "left"
        },
        grid: {
            padding: {
                top: 0,
                right: 0,
                bottom: 18,
                left: 0
            }
        },
        plotOptions: {
            pie: {
                customScale: 0.76,
                offsetY: -6,
                donut: {
                    size: "64%",
                    labels: {
                        show: true,
                        value: { formatter: (value) => formatWholeNumber(value) },
                        total: {
                            show: true,
                            label: typeof payload.tokenMixTotal === "string" ? payload.tokenMixTotal : "Total",
                            formatter: (chart) => formatWholeNumber(chart.globals.seriesTotals.reduce((sum, value) => sum + value, 0))
                        }
                    }
                }
            }
        },
        colors: ["#167bff", "#6c74f8"]
    });
}

function buildDistributionOptions(payload) {
    const data = readNamedValues(payload.distribution);
    const title = typeof payload.distributionTitle === "string" ? payload.distributionTitle : "";

    return withBaseOptions({
        chart: {
            type: "bar",
            height: 260,
            toolbar: { show: false },
            background: "transparent"
        },
        series: [
            {
                name: title,
                data: data.map((item) => item.value)
            }
        ],
        xaxis: {
            categories: data.map((item) => item.name),
            labels: {
                formatter: (value) => formatCompactNumber(value)
            }
        },
        yaxis: {
            labels: {
                maxWidth: 140
            }
        },
        tooltip: {
            y: {
                formatter: (value) => formatWholeNumber(value)
            }
        },
        legend: { show: false },
        plotOptions: {
            bar: {
                horizontal: true,
                borderRadius: 5,
                barHeight: "48%"
            }
        },
        colors: ["#167bff"]
    });
}

function withBaseOptions(options) {
    const baseGrid = {
        borderColor: "rgba(148, 163, 184, 0.2)",
        strokeDashArray: 4
    };

    return {
        ...options,
        dataLabels: {
            enabled: false,
            ...(options.dataLabels ?? {})
        },
        grid: {
            ...baseGrid,
            ...(options.grid ?? {})
        },
        legend: options.legend ?? {
            position: "top",
            horizontalAlign: "left",
            markers: { radius: 6 }
        },
        noData: {
            text: ""
        }
    };
}

function readStringArray(value) {
    if (!Array.isArray(value)) {
        return [];
    }

    return value.map((item) => (typeof item === "string" ? item : String(item ?? "")));
}

function readNumberArray(value, length) {
    const numbers = Array.isArray(value) ? value.map(toNumber) : [];
    if (!Number.isInteger(length) || length <= numbers.length) {
        return numbers;
    }

    return numbers.concat(Array.from({ length: length - numbers.length }, () => 0));
}

function readNamedValues(value) {
    if (!Array.isArray(value)) {
        return [];
    }

    return value
        .map((item) => ({
            name: typeof item?.name === "string" ? item.name : String(item?.name ?? ""),
            value: toNumber(item?.value)
        }))
        .filter((item) => item.name.length > 0 || item.value > 0);
}

function toNumber(value) {
    const number = Number(value);
    return Number.isFinite(number) ? Math.max(0, number) : 0;
}

function formatCompactNumber(value) {
    const number = toNumber(value);
    if (number >= 1000000) {
        return `${Math.round(number / 100000) / 10}M`;
    }

    if (number >= 1000) {
        return `${Math.round(number / 100) / 10}K`;
    }

    return formatWholeNumber(number);
}

function formatWholeNumber(value) {
    return Math.round(toNumber(value)).toLocaleString();
}
