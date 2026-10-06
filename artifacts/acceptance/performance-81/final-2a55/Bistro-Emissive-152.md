# Bistro 招牌自发光颜色与原始材质的对应错误

父验收：#70、#125。复现提交2a55ca9006e586072d11ea01193662deee567d4f，DefaultGame Amazon Bistro，RTX3080/D3D12，1280×720，TAA/async/mesh默认开启，固定1/60秒，实际scene/submitted63/63。

原始冻结模型Bistro.glb SHA2560ec1c93d10ae66feca5abb354690f2f7b5c6155f5562fab6e953e5ed657c498d。material19 Bistro_Sign_Letters（mesh143/源节点169 Bistro_Research_Exterior_Paris_BistroFrontBanner_01_4131_pr）声明emissiveFactor[1,0,0]、KHR_materials_emissive_strength100、emissiveTexture缺省。通过公共MaterialAsset.OpenAsync/MaterialBlock.GetParameter读取实际导入资产338be70d-76ca-40ef-96c7-1110933d41a9，EmissiveFactor正确为[100,0,0]，EmissiveMap是默认白纹理da8198af-5d1b-4804-ab6f-db822185c813。DefaultTextures.cs的白纹理像素[255,255,255,255]，ImportedSceneImporter.cs49–55将原始颜色乘强度写入该属性；材质shader按EmissiveMap×EmissiveFactor产生自发光。

使用原始mesh143三角形、实际捕获MeshAsset e562085a-5645-416b-827a-9f1ecc2db0e0的实例矩阵、实际view/带jitter projection投影，招牌覆盖屏幕bounds[460,186,678,227]，原三角形投影掩码2177像素。原始线性RGBA16F scene.rgba16在该区域的红通道最大0.0606384277，蓝通道最大100；823个投影掩码像素满足蓝色高能量（B>max(R,G)×2且B>2）。实际beauty区域图中Le Petit Coin字样为蓝色。线性数据位于TAA/tone map之前，颜色差异已经存在于此输出。

原始stringlight材质115/120–124包含白、红、蓝、绿、粉、橙色自发光；蓝色灯泡121是源模型设计的一部分。public读取其导入向量依次为[8,8,8]、[8,0,0]、[0,0,8]、[0,8,0]、[8,0,8]、[8,1.5,0]。这些事实区分了允许的蓝色灯泡与红色招牌产生蓝色的可定位输出差异。

下一步沿材质标量写入、shader反射字段偏移、材质索引/绑定与像素输出链定位根因，并以公共渲染入口建立已知RGB自发光颜色回归。该证据独立于方向光VSM掩码：现默认三场景RT3/3通过只验证对应的可见性/覆盖/误差门槛。

证据：bistro-original-emissive-surfaces.json（原始材质/源mesh/node关系）；bistro-source-material-values.json（实际导入公共参数）；bistro-public-material-read-provenance.json（临时CPU读程序hash、公共入口、0device、清理记录）；bistro-sign-color-attribution.json（实际帧/投影/线性RGB计数）；bistro-sign-original-geometry-mask.png；bistro-sign-beauty-region.png；final-default-three/ray-shadow-bistro-default-0de42d7b1fca43a9971ea97337d348b5/{scene.bmp,scene.png,scene.rgba16,scene.rgba16.json,comparison.json}。

投影掩码保留所有原招牌三角形，可能包含轮廓/遮挡像素；它用于已知材质表面的对应定位。方向光独立参考的完整接收者、门槛和误差输出持续保留。
