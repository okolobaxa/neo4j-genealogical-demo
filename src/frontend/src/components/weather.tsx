// Generative UI: rendered on the frontend when the agent calls the get_weather tool.
export function WeatherCard({
  location,
  themeColor,
}: {
  location?: string;
  themeColor: string;
}) {
  return (
    <div className="gen-card" style={{ backgroundColor: themeColor }}>
      <div className="gen-card-inner">
        <div className="gen-card-head">
          <div>
            <h3 className="gen-card-title">{location ?? "Somewhere"}</h3>
            <p className="gen-card-muted">Current Weather</p>
          </div>
          <div className="gen-card-icon">☀️</div>
        </div>
        <div className="weather-temp-row">
          <div className="weather-temp">70°</div>
          <div className="gen-card-muted">Clear skies</div>
        </div>
        <div className="weather-grid">
          <div>
            <p className="gen-card-muted">Humidity</p>
            <p className="weather-metric">45%</p>
          </div>
          <div>
            <p className="gen-card-muted">Wind</p>
            <p className="weather-metric">5 mph</p>
          </div>
          <div>
            <p className="gen-card-muted">Feels Like</p>
            <p className="weather-metric">72°</p>
          </div>
        </div>
      </div>
    </div>
  );
}
