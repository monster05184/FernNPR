using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FernGraph;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Random = UnityEngine.Random;

namespace FernNPRCore.StableDiffusionGraph
{
    [Node(Path = "SD Standard")]
    [Tags("SD Node")]
    public class SDSamplerNode : SDFlowNode, ICanExecuteSDFlow
    {
        [Input] public Prompt Prompt;
        [Input("ControlNet")] public ControlNetData controlNetData;
        [Input] public int Step = 20;
        [Input] public int CFG = 7;
        [Output("Out Image")] public Texture2D OutputImage;
        [Output("Seed")] public long outSeed;

        public Action<long, long> OnUpdateSeedField;

        public long Seed = -1;
        public string SamplerMethod = "Euler";

        private int width = 512;
        private int height = 512;
        private float aspect;


        public override IEnumerator Execute()
        {
            Prompt = GetInputValue("Prompt", this.Prompt);
            controlNetData = GetInputValue("ControlNet", controlNetData);
            var vec2 = SDUtil.GetMainGameViewSize();
            width = (int)vec2.x;
            height = (int)vec2.y;
            if (Seed == 0)
                Seed = -1;
            yield return (GenerateAsync());
        }
        
        long GenerateRandomLong(long min, long max)
        {
            byte[] buf = new byte[8];
            new System.Security.Cryptography.RNGCryptoServiceProvider().GetBytes(buf);
            long longRand = BitConverter.ToInt64(buf, 0);
            return (Math.Abs(longRand % (max - min)) + min);
        }

        IEnumerator GenerateAsync()
        {
            long seed = Seed;
            if (seed == -1)
                seed = GenerateRandomLong(-1, Int64.MaxValue);
            // Generate the image
            HttpWebRequest httpWebRequest = null;
            try
            {
                // Make a HTTP POST request to the Stable Diffusion server
                //var txt2ImgAPI = controlNet == defaultControlNet ? SDDataHandle.Instance.TextToImageAPI : SDDataHandle.Instance.ControlNetTex2Img;
                var txt2ImgAPI = SDDataHandle.Instance.TextToImageAPI;
                httpWebRequest = (HttpWebRequest)WebRequest.Create(SDDataHandle.Instance.GetServerURL() + txt2ImgAPI);
                httpWebRequest.ContentType = "application/json";
                httpWebRequest.Method = "POST";

                // add auth-header to request
                if (SDDataHandle.Instance.GetUseAuth() && !string.IsNullOrEmpty(SDDataHandle.Instance.GetUserName()) && !string.IsNullOrEmpty(SDDataHandle.Instance.GetPassword()))
                {
                    httpWebRequest.PreAuthenticate = true;
                    byte[] bytesToEncode = Encoding.UTF8.GetBytes(SDDataHandle.Instance.GetUserName() + ":" + SDDataHandle.Instance.GetPassword());
                    string encodedCredentials = Convert.ToBase64String(bytesToEncode);
                    httpWebRequest.Headers.Add("Authorization", "Basic " + encodedCredentials);
                }

                string json;
                // Send the generation parameters along with the POST request
                using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
                {
                    if (controlNetData == null)
                    {
                        SDParamsInTxt2Img sd = new SDParamsInTxt2Img();
                        sd.prompt = Prompt.positive;
                        sd.negative_prompt = Prompt.negative;
                        sd.steps = Step;
                        sd.cfg_scale = CFG;
                        sd.width = width;
                        sd.height = height;
                        sd.seed = seed;
                        sd.tiling = false;
                        sd.sampler_name = SamplerMethod;
                        // Serialize the input parameters
                        json = JsonConvert.SerializeObject(sd);
                    }
                    else
                    {
                        SDParamsInTxt2ImgContronlNet sd = new SDParamsInTxt2ImgContronlNet();
                        sd.prompt = Prompt.positive;
                        sd.negative_prompt = Prompt.negative;
                        sd.steps = Step;
                        sd.cfg_scale = CFG;
                        sd.width = width;
                        sd.height = height;
                        sd.seed = seed;
                        sd.tiling = false;
                        sd.sampler_name = SamplerMethod;
                        if (controlNetData != null)
                        {
                            SDUtil.Log("use controlnet");
                            sd.alwayson_scripts = new ALWAYSONSCRIPTS();
                            sd.alwayson_scripts.controlnet = new ControlNetDataArgs();
                            sd.alwayson_scripts.controlnet.args = new[] { controlNetData };
                        }
                        // Serialize the input parameters
                        json = JsonConvert.SerializeObject(sd);
                    }
                    
                    // Send to the server
                    streamWriter.Write(json);
                }
            }
            catch (Exception e)
            {
                SDUtil.LogError(e.Message + "\n\n" + e.StackTrace);
            }

            // Read the output of generation
            if (httpWebRequest != null)
            {
                // Wait that the generation is complete before procedding
                Task<WebResponse> webResponse = httpWebRequest.GetResponseAsync();

                while (!webResponse.IsCompleted)
                {
                    if (SDDataHandle.Instance.GetUseAuth() && !string.IsNullOrEmpty(SDDataHandle.Instance.GetUserName()) && !string.IsNullOrEmpty(SDDataHandle.Instance.GetPassword()))
                        //UpdateGenerationProgressWithAuth();
                   // else
                       // UpdateGenerationProgress();

                    yield return new WaitForSeconds(0.5f);
                }

                // Stream the result from the server
                var httpResponse = webResponse.Result;

                using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                {
                    // Decode the response as a JSON string
                    string result = streamReader.ReadToEnd();
                    
                    // Deserialize the JSON string into a data structure
                    SDResponseTxt2Img json = JsonConvert.DeserializeObject<SDResponseTxt2Img>(result);

                    // If no image, there was probably an error so abort
                    if (json.images == null || json.images.Length == 0)
                    {
                        SDUtil.LogError(
                            "No image was return by the server. This should not happen. Verify that the server is correctly setup.");

#if UNITY_EDITOR
                        EditorUtility.ClearProgressBar();
#endif
                        yield break;
                    }

                    // Decode the image from Base64 string into an array of bytes
                    byte[] imageData = Convert.FromBase64String(json.images[0]);
                    OutputImage = new Texture2D(width, height, DefaultFormat.HDR, TextureCreationFlags.None);
                    OutputImage.LoadImage(imageData);

                    try
                    {
                        // Read the generation info back (only seed should have changed, as the generation picked a particular seed)
                        if (json.info != "")
                        {
                            SDParamsOutTxt2Img info = JsonConvert.DeserializeObject<SDParamsOutTxt2Img>(json.info);

                            // Read the seed that was used by Stable Diffusion to generate this result
                            outSeed = info.seed;
                            OnUpdateSeedField?.Invoke(Seed,outSeed);
                        }
                    }
                    catch (Exception e)
                    {
                        SDUtil.LogError(e.Message + "\n\n" + e.StackTrace);
                    }
                }
            }
            yield return null;
        }

        public override object OnRequestValue(Port port)
        {
            if (port.Name == "Out Image")
            {
                return OutputImage;
            }else if (port.Name == "Seed")
            {
                return outSeed;
            }

            return null;
        }
    }
}