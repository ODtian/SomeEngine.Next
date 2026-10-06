# #142 direct light identity and feedback parameters

`DirectLightUpdatesKeepTheirEntityWhenInputOrderChanges` uses the public RenderLightSet Entity overload, revisits two point lights in reverse input order and checks each entity's final public light data. API red: CS1503 while Entity overloads were absent. Green: one behavior row passed.

The Entity overload shares the same row update used by collected RenderWorld lights. Per-shadow history therefore uses source Entity plus face or absolute clipmap level. The global maximum is exposed as DynamicResolutionMaxLodBias (native default 2), and both page and work pressure consume that same maximum. Work directional/local limits remain native 99999. The obsolete material-batch budget option is removed; material page area stays a public diagnostic, and the page-pressure kernel reads page-pool data independently of the raster work buffer.
